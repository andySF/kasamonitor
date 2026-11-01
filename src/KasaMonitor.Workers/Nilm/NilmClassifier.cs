using KasaMonitor.Core.Nilm;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace KasaMonitor.Workers.Nilm;

/// <summary>One row of the confusion matrix: how the events of class <see cref="Actual"/> were predicted (counts aligned to <see cref="NilmModelInfo.Classes"/>).</summary>
public sealed record NilmConfusionRow(string Actual, IReadOnlyList<int> Predicted);

/// <summary>Centre of one class of the training set (means as for a signature), for the open-set check of predictions.</summary>
public sealed record NilmClassCentre(string Label, double MeanDeltaP, double MeanDeltaQ, double MeanTransient, bool HasOnEvents)
{
    public SignatureCentre Centre => new(0, MeanDeltaP, MeanDeltaQ, MeanTransient, HasOnEvents);
}

/// <summary>The last training: measured accuracy (5-fold cross-validation), classes, confusion matrix and class centres.</summary>
public sealed record NilmModelInfo(DateTimeOffset TrainedUtc, int Examples, IReadOnlyList<string> Classes, double Accuracy, int Folds,
    IReadOnlyList<NilmConfusionRow> Confusion, IReadOnlyList<NilmClassCentre>? Centres = null)
{
    /// <summary>
    /// Open-set check: a prediction is used only when the event is also close to the centre of the predicted class
    /// (within <see cref="NilmClassifier.CentreTolerance"/> × the clustering eps); an appliance never seen by the plugs
    /// (a lamp without a plug) is otherwise forced into the nearest known class.
    /// </summary>
    public bool NearClass(MeterEvent e, string label) =>
        Centres?.FirstOrDefault(c => c.Label == label) is { } c
        && SignatureClusterer.Distance(EventFeatures.Of(e), c.Centre, NilmClassifier.CentreTolerance) <= 1;
}

/// <summary>
/// Multiclass classifier (ML.NET <c>SdcaMaximumEntropy</c>) over the labelled events (labels from plugs and from the user).
/// Features: ΔP, log(1 + |ΔP|), ΔQ, transient ratio, rise seconds, hour of day as sin/cos, day of week (local time).
/// Trained when there are at least <see cref="MinExamples"/> labelled events in at least <see cref="MinClasses"/> classes;
/// the accuracy is measured by k-fold cross-validation (every event predicted by a model that did not see it), then the
/// final model is trained on everything. A prediction is used only when its probability is ≥ <see cref="MinProbability"/>.
/// </summary>
public sealed class NilmClassifier
{
    public const int MinExamples = 20;
    public const int MinClasses = 2;
    public const double MinProbability = 0.7;
    public const int DefaultFolds = 5;

    /// <summary>A prediction is used only within this many clustering eps of the predicted class's centre.</summary>
    public const double CentreTolerance = 2;

    private sealed class Row
    {
        public float DeltaP { get; set; }
        public float LogAbsP { get; set; }
        public float DeltaQ { get; set; }
        public float Transient { get; set; }
        public float Rise { get; set; }
        public float HourSin { get; set; }
        public float HourCos { get; set; }
        public float DayOfWeek { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class Prediction
    {
        public string PredictedLabel { get; set; } = "";
        public float[] Score { get; set; } = Array.Empty<float>();
    }

    private static readonly string[] FeatureColumns =
        { nameof(Row.DeltaP), nameof(Row.LogAbsP), nameof(Row.DeltaQ), nameof(Row.Transient), nameof(Row.Rise),
          nameof(Row.HourSin), nameof(Row.HourCos), nameof(Row.DayOfWeek) };

    private readonly MLContext _ml = new(seed: 1);
    private readonly TimeZoneInfo _tz;
    private readonly object _lock = new();
    private ITransformer? _model;
    private PredictionEngine<Row, Prediction>? _engine;

    public NilmClassifier(TimeZoneInfo? tz = null) => _tz = tz ?? TimeZoneInfo.Local;

    public bool HasModel { get { lock (_lock) return _model is not null; } }

    public static bool CanTrain(IReadOnlyCollection<MeterEvent> labeled) =>
        labeled.Count(e => !string.IsNullOrWhiteSpace(e.Label)) >= MinExamples
        && labeled.Where(e => !string.IsNullOrWhiteSpace(e.Label)).Select(e => e.Label).Distinct(StringComparer.Ordinal).Count() >= MinClasses;

    private Row Features(MeterEvent e, string label = "")
    {
        var local = TimeZoneInfo.ConvertTime(e.TsUtc, _tz);
        var hour = local.Hour + local.Minute / 60.0;
        var angle = 2 * Math.PI * hour / 24.0;
        return new Row
        {
            DeltaP = (float)e.DeltaP,
            LogAbsP = (float)Math.Log(1 + Math.Abs(e.DeltaP)),
            DeltaQ = (float)e.DeltaQ,
            Transient = (float)e.TransientRatio,
            Rise = (float)e.RiseSeconds,
            HourSin = (float)Math.Sin(angle),
            HourCos = (float)Math.Cos(angle),
            DayOfWeek = (int)local.DayOfWeek,
            Label = label,
        };
    }

    private IEstimator<ITransformer> Pipeline() =>
        _ml.Transforms.Conversion.MapValueToKey("LabelKey", nameof(Row.Label))
            .Append(_ml.Transforms.Concatenate("Features", FeatureColumns))
            .Append(_ml.Transforms.NormalizeMeanVariance("Features"))
            .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy("LabelKey", "Features", maximumNumberOfIterations: 200))
            .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

    /// <summary>
    /// Cross-validates (k folds, deterministic shuffle), then trains the final model on every labelled event and keeps it.
    /// Returns null when there are not enough labels (<see cref="CanTrain"/>).
    /// </summary>
    public NilmModelInfo? Train(IReadOnlyList<MeterEvent> labeled, DateTimeOffset nowUtc, int folds = DefaultFolds)
    {
        var rows = labeled.Where(e => !string.IsNullOrWhiteSpace(e.Label)).Select(e => Features(e, e.Label!.Trim())).ToList();
        if (!CanTrain(labeled)) return null;
        var classes = rows.Select(r => r.Label).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var index = classes.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.Ordinal);
        var matrix = classes.Select(_ => new int[classes.Count]).ToArray();

        var rnd = new Random(1);
        var fold = rows.Select(_ => rnd.Next(folds)).ToArray();
        int correct = 0, predicted = 0;
        for (var f = 0; f < folds; f++)
        {
            var train = rows.Where((_, i) => fold[i] != f).ToList();
            var test = rows.Where((_, i) => fold[i] == f).ToList();
            if (test.Count == 0 || train.Select(r => r.Label).Distinct().Count() < MinClasses) continue;
            var model = Pipeline().Fit(_ml.Data.LoadFromEnumerable(train));
            var output = _ml.Data.CreateEnumerable<Prediction>(model.Transform(_ml.Data.LoadFromEnumerable(test)), reuseRowObject: false).ToList();
            for (var i = 0; i < test.Count; i++)
            {
                predicted++;
                if (output[i].PredictedLabel == test[i].Label) correct++;
                if (index.TryGetValue(output[i].PredictedLabel, out var p)) matrix[index[test[i].Label]][p]++;
            }
        }

        var final = Pipeline().Fit(_ml.Data.LoadFromEnumerable(rows));
        lock (_lock)
        {
            _engine?.Dispose();
            _model = final;
            _engine = _ml.Model.CreatePredictionEngine<Row, Prediction>(final);
        }
        var centres = labeled.Where(e => !string.IsNullOrWhiteSpace(e.Label)).GroupBy(e => e.Label!.Trim(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var (p, q, t) = SignatureClusterer.Means(g.Select(EventFeatures.Of).ToList());
                return new NilmClassCentre(g.Key, p, q, t, g.Any(e => e.DeltaP > 0));
            }).ToList();
        return new NilmModelInfo(nowUtc, rows.Count, classes, predicted > 0 ? (double)correct / predicted : 0, folds,
            classes.Select((c, i) => new NilmConfusionRow(c, matrix[i])).ToList(), centres);
    }

    /// <summary>The predicted label and its probability (the softmax score of that class), or null without a model.</summary>
    public (string Label, double Probability)? Predict(MeterEvent e)
    {
        lock (_lock)
        {
            if (_engine is null) return null;
            var p = _engine.Predict(Features(e));
            if (string.IsNullOrEmpty(p.PredictedLabel) || p.Score.Length == 0) return null;
            return (p.PredictedLabel, p.Score.Max());
        }
    }

    public void Save(string path)
    {
        lock (_lock)
        {
            if (_model is null) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var schema = SchemaDefinition.Create(typeof(Row));
            _ml.Model.Save(_model, _ml.Data.LoadFromEnumerable(Array.Empty<Row>(), schema).Schema, path);
        }
    }

    /// <summary>Loads a saved model; false (and no model) when the file is missing or unreadable.</summary>
    public bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var model = _ml.Model.Load(path, out _);
            lock (_lock)
            {
                _engine?.Dispose();
                _model = model;
                _engine = _ml.Model.CreatePredictionEngine<Row, Prediction>(model);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Forgets the model (ML switched off).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
            _model = null;
        }
    }
}
