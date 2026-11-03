# Appliance detection (NILM)

How KasaMonitor recognises appliances in the power of the whole house, for a technical reader. This is the
conceptual explanation; thresholds, settings, metrics and every UI detail are in the README, section
[Appliance detection (NILM)](../README.md#appliance-detection-nilm), and the HTTP routes in
[HTTP API (LAN)](../README.md#http-api-lan). Why some rules look the way they do is in [decisions.md](decisions.md)
(0.8.2, 0.9.1, 0.9.4).

NILM (non-intrusive load monitoring) tries to say *which appliance* is on from one measurement at the mains entry. The
approach here is deliberately simple, deterministic and explainable: every grouping and every merge can be
justified by a few numbers, and the plugs that are measured anyway provide labels for free.

```
 main meter 1 s power (P, S)
        │
        ▼
 ┌────────────────┐   ΔP, ΔQ, Δpf, transient, rise time
 │ event detector │ ───────────────────────────────────────────────► MeterEvents
 └────────────────┘                                                      │
        plug readings (10 s polls + ~3 s stream) ──► plug matcher ───────┤ label "auto-plug"
                                                                          ▼
                       anchors (named / plug-linked signatures) ► incremental assignment
                       nightly: anchors first, then DBSCAN, then automatic merge
                                                                          ▼
                              ApplianceSignatures ─► classifier (ML.NET, optional)
                                                                          ▼
                             switch-on ↔ switch-off pairing ─► ApplianceRuns ─► energy per day
```

Requirement: a **main meter** with the live stream on (1 s). Without it nothing here runs.

## 1. Events: steps in a steady power

The detector works on the main meter's 1-second active power (and apparent power, from which the reactive power is
derived).

- The level *before* is the mean of the last 5 s of a steady state. A second outside max(5 W, 3 %) of that level
  starts a transition; it ends when 3 consecutive seconds lie within max(5 W, 3 %) of their own mean, which is the
  level *after*. The step is an event if |ΔP| ≥ a minimum (default 30 W).
- The **features** of an event:
  - **ΔP**, the signed step in watts;
  - **ΔQ**, the change of reactive power |Q| = √(S² − P²) (distinguishes resistive from inductive loads);
  - **Δpf**, the change of power factor;
  - **transient ratio**, the peak in the first 2 s divided by ΔP (resistive ≈ 1, motors 3–8, which is the inrush);
  - **rise time**, and the time of day.
- Two appliances switched 2 s apart give two events (a plateau of ≥ 2 s inside a transition); a peak that falls back
  (inrush) gives one event with a high transient ratio; slow drift moves the level and produces no event.
- A missing second is filled with the previous value for up to 65 s (the raw table keeps a row only on change). A
  duplicate within ±2 s, same direction and size, is dropped, so restarts do not double-count.
- **Unknown transient.** If the step reached its new level within a single sample, the measured peak is trustworthy.
  If it took ≥ 1 s *and* the ratio is ≤ 1.5, the measurement started late or a small wobble opened the transition
  early, so the true peak may have been missed: the transient is *unknown*. A ratio above 1.5 can only be real. An
  unknown transient is stored as measured but never compared, clustered, merged, or averaged.

At start the stored stream rows after the last processed marker are replayed through the detector in 6-hour batches
(the *backfill*), so the history is rebuilt after a gap or an upgrade.

## 2. Labels from the plugs

The plugs already measure some appliances. 45 s after an event, the plugs' readings around it are compared:

- A plug's readings are its stream (≈ 3 s, held on a 1-second grid, so a stable level counts every second and a ramp
  only for the seconds it lasted) when that covers both windows, otherwise its 10-second polls.
- The plug's level before is the median of its readings in [t − 20 s, t − 3 s], after is the median in
  [t + 3 s, t + 20 s]. Readings within ±3 s of the event are ignored, so a value taken in the middle of a ramp does not
  hide the step. Both windows stop 3 s before a neighbouring main-meter event large enough to move the plug.
- The plug labels the event (source `auto-plug`) when its step equals ΔP within max(10 W, 15 %), it is the **only** plug
  whose level moved the same way by ≥ 60 % of |ΔP| (a UPS moving at the same time makes the event ambiguous), and
  that plug step has not labelled another event.
- A plug that matches more than 20 events in an hour (a UPS, a PC) is a variable load and is excluded from learning
  automatically; this can be undone by hand.

When ≥ 3 events and ≥ 60 % of a signature's events come from the same plug, the signature is **linked** to the plug:
its consumption is measured by the plug itself, so it is listed separately, kept out of „Acum pornite” and from the
detected energy, and never subtracted twice from the rest of the house. A plug carrying a single appliance
skips the thresholds (see decisions, 0.9.1).

## 3. Signatures: grouping events

A **signature** is a group of similar events, i.e. an appliance *component*: a washing machine is a heater (≈ 2 kW), a
drum motor (≈ 250–300 W pulses) and a pump; a kettle is one heater.

- **Clustering** is DBSCAN (own implementation, memory O(n)) over |ΔP| on a relative scale, ΔQ seen from the switch-on
  side and the transient ratio of switch-ons. Tolerances: eps(|ΔP|) = 8 % of |ΔP| + 3 W, eps(ΔQ) = 8 % of |ΔP| + 15 VAR,
  minimum 3 points; a cluster wider than (1 + 2 × 8 %) of its smallest |ΔP| + 6 W is cut into narrower bands, because a
  dense band of small loads would otherwise chain into one cluster. Switch-ons and switch-offs of the same size join
  the same signature.
- **Anchors.** A signature named by the user or linked to a plug is an anchor. A new event goes first to the nearest
  anchor within 1.5 × eps (an event labelled by a plug first to the anchor linked to that plug, never to one linked
  to another), otherwise to the nearest other signature within eps, otherwise to a new „Necunoscut #n”. The wider
  tolerance lets a kettle running at −6 % (lower voltage while a washer heats) still land on its name.
- **Nightly re-clustering** (03:30, at start when the last run is older than 24 h, or on demand): the newest 20,000
  events of the last 30 days are clustered again on a low-priority thread. Events labelled by the user or by the
  plug of their signature never move. Every other event passes the anchors again (it stays in a labelled signature only
  while within 1.5 × eps, so a signature cannot ratchet away by keeping everything it once caught); DBSCAN groups the
  rest; empty automatic signatures are deleted.
- **Automatic merge.** Several signatures that are the same component are merged, never across two different plugs,
  never across two different user-given names, never hidden ones. Rules: (c) the same user name and centres within
  1.5 × eps; (a) centres within eps (transient compared only if both know it); (b) both linked to the same plug, or
  clearly „possible” for it, with |ΔP| within 15 %; (d) the same **inductive component with a variable load** (a drum
  motor's pulses): neither is an anchor, |ΔQ/ΔP| ≥ 0.5 and |ΔP| ≥ 150 W for both, ΔQ/ΔP within 0.15 and |ΔP| within 25 %,
  and at least 60 % of the smaller one's events have an event of the other within ±10 minutes, twice as often as with
  the times shifted by ±1 hour (a dense small load is „together” with everything by chance). The survivor is the
  user-named one, else the plug-linked one, else the one with more events; the absorbed signature's events and all its
  runs move to it.
- **The „sheet”** of a signature (ΔP mean ± deviation, ΔQ, power factor, transient or „unknown n of m”, typical run
  length with percentiles, a 24-bar histogram of hours, plug share, energy over 7 days) is computed on demand from the
  events and runs, by the same function that feeds the automatic merge, so what the user sees is what the rule used.

## 4. Classifier (optional)

With at least 20 events labelled by plugs or the user, in at least two classes, a multiclass ML.NET model
(`SdcaMaximumEntropy`; features ΔP, log |ΔP|, ΔQ, transient, rise, hour of day as sin/cos, day of week) is trained
nightly, after each re-clustering and on demand. Accuracy is measured by 5-fold cross-validation and shown with a
confusion matrix — **only over appliances that have a plug or a user label**, so it says nothing about the rest of
the house. A prediction is used for a new event without a plug label only when the probability is ≥ 0.7 *and* the
event lies within 2 × eps of the class centre (an open-set check: an appliance no plug has seen would otherwise be
forced into the nearest known class). The classifier supplements the clustering; it never replaces it.

## 5. Runs and energy

A switch-on opens a **run**. It closes with the next switch-off of the same signature whose size is within eps (first in,
first out), or with any switch-off that brings the house back to (or below) the level it had before that
switch-on — a simple heuristic that is wrong when something else turned on in between. A run open longer than 6 hours
is closed as uncertain with *no energy*: its switch-off was never seen, so it counts as an „uncertain run” and not as
consumption. Energy of a run is ΔP × duration, or, for a paired run with no other event during it, the house's mean
power above its previous level (kept only between ½ and 2× the nominal value). Energy is split per day per
signature (a run across midnight proportionally). **„Neidentificat”** (unidentified) = the house today − Σ plugs −
Σ detected runs, never below 0.

## 6. What it cannot detect — known limits

- **No steady state, no event.** Appliances with a continuously variable power (induction hobs, inverter compressors
  and heat pumps, dimmers) do not settle within 30 s, so they produce no event or many small ones.
- **Slow ramps.** Slow drift moves the level without an event; a ramp faster than ≈ 3 W/s on a low base can be seen
  as a step.
- **Short pulses.** The plug matcher compares *stabilised levels* (medians before and after). A pulse shorter than
  about 15 s returns to the baseline, so the difference of medians is near zero and the plug does not label it,
  although the plug saw the peak.
- **Small loads, 15–50 W.** A UPS, lamps, chargers and a PC have nearly the same ΔP, ΔQ and transient. Their
  signatures stay mixed; lowering the threshold to 15 W gave 76 events in 16 minutes, mostly from a UPS. The meter itself
  moves by a median of 17 W per second.
- **Simultaneous switching.** Two appliances switched within 2 s in opposite directions give one net step; two
  large steps less than ≈ 7 s apart, or two plugs moving together, give no plug label.
- **Similar power, different shape.** An espresso machine (pulses of ≈ 1.4 kW) and a kettle (a continuous ≈ 1.5 kW) look alike
  by ΔP; only duration and form tell them apart.
- **ΔQ is a whole-house difference.** A capacitive load (a switched-mode supply) lowers |Q| in an inductive house and
  raises it in a house with Q ≈ 0, so its sign depends on what else is running; this is why the eps for ΔQ is wide.
- **Multi-stage appliances** appear as several signatures (one per component); the pairing assumes one switch-off per
  switch-on.
- **Plug offline** means no label for that period.

## 7. Open ideas

- **Detection on the rest.** Run an event detector on *meter − Σ plugs* instead of on the meter, so the measured
  appliances stop masking the unmeasured ones, and so that smaller agitated loads (a massage chair, measured at ≈ +50 W
  on average with no step ≥ 100 W) become visible.
- **Duration as a feature.** Use the typical run length from the sheet as an extra distance term when matching an
  anchor, to separate pulsed from continuous loads of equal power (espresso machine vs kettle: ≥ 60 s continuous vs
  pulses of under 20 s).
- **Peak-based plug matching for short events** (a window around the event looking for a peak, not for a changed
  level), to label the pulses described above.
