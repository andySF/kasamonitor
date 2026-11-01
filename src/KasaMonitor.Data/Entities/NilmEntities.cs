namespace KasaMonitor.Data.Entities;

/// <summary>One detected step of the main meter (table MeterEvents). <see cref="Ts"/> = unix seconds, UTC.</summary>
public sealed class MeterEventEntity
{
    public long Id { get; set; }
    public Guid PlugId { get; set; }
    public long Ts { get; set; }
    public double DeltaP { get; set; }
    public double DeltaQ { get; set; }
    public double DeltaPf { get; set; }
    public double TransientRatio { get; set; }
    public double RiseSeconds { get; set; }
    public double PowerBeforeW { get; set; }
    public long? SignatureId { get; set; }
    public string? Label { get; set; }
    /// <summary><see cref="KasaMonitor.Core.Nilm.EventLabelSource"/>: 0 none, 1 auto-plug, 2 user, 3 model.</summary>
    public int Source { get; set; }
    public Guid? LabelPlugId { get; set; }
}

/// <summary>One appliance signature (table ApplianceSignatures). <see cref="LastSeen"/> = unix seconds, UTC.</summary>
public sealed class ApplianceSignatureEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "";
    public double MeanDeltaP { get; set; }
    public double MeanDeltaQ { get; set; }
    public double MeanTransient { get; set; }
    public int Count { get; set; }
    public long? LastSeen { get; set; }
    /// <summary><see cref="KasaMonitor.Core.Nilm.SignatureLabelSource"/>: 0 cluster, 1 plug, 2 user.</summary>
    public int LabelSource { get; set; }
    public Guid? PlugId { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>One run of an appliance (table ApplianceRuns). <see cref="StartTs"/>/<see cref="EndTs"/> = unix seconds, UTC.</summary>
public sealed class ApplianceRunEntity
{
    public long Id { get; set; }
    public long SignatureId { get; set; }
    public long StartTs { get; set; }
    public long? EndTs { get; set; }
    public double PowerW { get; set; }
    public double? EstimatedWh { get; set; }
    public long StartEventId { get; set; }
    public long? EndEventId { get; set; }
    public double HouseBeforeW { get; set; }
    /// <summary><see cref="KasaMonitor.Core.Nilm.RunEndReason"/>: 0 paired, 1 house below level, 2 timeout (uncertain).</summary>
    public int EndReason { get; set; }
}
