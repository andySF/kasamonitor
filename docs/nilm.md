# Appliance detection (NILM)

How KasaMonitor recognises appliances in the power of the whole house, for a technical reader. This is the
conceptual explanation; thresholds, settings, metrics and every UI detail are in the README, section
[Appliance detection (NILM)](../README.md#appliance-detection-nilm), and the HTTP routes in
[HTTP API (LAN)](../README.md#http-api-lan). Why some rules look the way they do is in [decisions.md](decisions.md)
(0.8.2, 0.9.1, 0.9.4, 0.13.0, 0.13.1, 0.13.2).

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
                                                                          ▲
                                       duration bands: a closed run out of its band moves ┘
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
  automatically; this can be undone by hand. A plug excluded from learning does not label, but still makes an event
  ambiguous. A plug with a single appliance labels and links whatever its state of learning, and never trains the classifier.

When ≥ 3 events and ≥ 60 % of a signature's events come from the same plug, the signature is **linked** to the plug:
its consumption is measured by the plug itself, so it is listed separately, kept out of „Acum pornite” and from the
detected energy, and never subtracted twice from the rest of the house. A plug carrying a single appliance
skips the thresholds (see decisions, 0.9.1).

**The signature of a plug with a single appliance is exclusive** (decisions, 0.13.1): it holds only events that plug
confirmed and events the user put there by hand. Nothing else enters it, on any path. A step the plug could not confirm
(a pulse under ≈ 15 s, section 7) therefore leaves it, even though the plug did measure it; the plug's own energy is
unaffected, and the number of events in „Necunoscut” goes up.

**The rest of that plug's story comes from the plug** (decisions, 0.13.2). Matching does not reach far: the main meter has an event
about every 11 s and a threshold of 30 W, so most steps of a small appliance are lost in it. Measured on 68 h of a fridge, only
31 % of its transitions have one candidate among the events, and widening the window adds chance coincidences faster than real
ones. So a confirmation stays strict (above), and the rest is read from the plug's own power:

- **Transitions.** On a 1-second grid (its stream, otherwise its polls, a row holding at most 70 s), the plug is on from 20 W. A
  change is real when it still holds 5 s after its first second, which is its moment; its step is the median of [t + 3 s, t + 20 s]
  minus that of [t − 20 s, t − 3 s]. Without data there is no transition across the gap. On the same 68 h this gives 242 transitions.
  A plug without a stream must therefore be polled at least every 70 s: with rarer polls every hole restarts the state, so it gives no
  transition and no run, while its runs from events are still replaced.
- **Shadows.** An event with no plug's or user's word on it (source none or model) is a **shadow** of the plug when it is the only
  candidate of a transition: the same sign, |ΔP − step| ≤ 15 W, at most 5 s before it. It gets the source `plug-shadow`, no label and
  no signature: it forms no run and is no longer subtracted from the rest of the house. It is not a confirmation, so it never feeds
  the centre or the classifier. A confirmed event in the window counts as a candidate too, and then nothing is marked; an event is a
  shadow once. Because a step needs the 20 s after it, a transition is known after its event was stored, so the mark corrects it:
  the event leaves its signature and the runs of that signature are built again. The user can label a shadow, and then it is the user's.

A step of the appliance that is neither confirmed nor a shadow (two candidates, or a step measured far from the plug's) still lands in
an unknown signature, so its run counts as detected energy and is subtracted from the rest of the house, while the plug
measures the same energy too. The rest of the house is a little too low by that much. Section 7 has the opposite limit.

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
  tolerance lets a kettle running at −6 % (lower voltage while a washer heats) still land on its name. An exclusive
  anchor (section 2) takes only the events labelled by its own plug; for the others it does not exist. A prediction of the
  classifier cannot give an event to one either: a name that points only at exclusive signatures counts as no label, a name
  with no signature keeps its label and goes through the anchors like any event.
- **Nightly re-clustering** (03:30, at start when the last run is older than 24 h, or on demand): the newest 20,000
  events of the last 30 days are clustered again on a low-priority thread. Events labelled by the user or by the
  plug of their signature never move. Every other event passes the anchors again (it stays in a labelled signature only
  while within 1.5 × eps, so a signature cannot ratchet away by keeping everything it once caught); DBSCAN groups the
  rest; empty automatic signatures are deleted. In an exclusive signature only what the plug confirmed and what the user
  labelled stays; the rest goes through the anchors and DBSCAN again. Events the job does not read leave too, without a
  signature: inside the window but past the newest 20,000 they would keep feeding the centre, and older than the window
  they feed nothing but would stay on the sheet. Centre and count are rebuilt from what stays. An emptied exclusive
  signature is not deleted: it is the plug's, its centre shows 0, and the next confirmation sets it. Shadows are fixed: DBSCAN and
  the anchors do not touch them. The job first calculates them again where the plug has data, with the same result for the same
  data: an event that became the only candidate is marked, and a shadow nothing explains any more goes through the anchors and
  DBSCAN like any event. A confirmation never makes a new signature only because a
  stored centre drifted: it first looks at the confirmed events behind each centre of its plug, then at a signature with
  none, and only then starts a new one (a new component).
- **Automatic merge.** Several signatures that are the same component are merged, never across two different plugs,
  never across two different user-given names, never hidden ones. Rules: (c) the same user name and centres within
  1.5 × eps; (a) centres within eps (transient compared only if both know it); (b) both linked to the same plug, or
  clearly „possible” for it, with |ΔP| within 15 %; (d) the same **inductive component with a variable load** (a drum
  motor's pulses): neither is an anchor, |ΔQ/ΔP| ≥ 0.5 and |ΔP| ≥ 150 W for both, ΔQ/ΔP within 0.15 and |ΔP| within 25 %,
  and at least 60 % of the smaller one's events have an event of the other within ±10 minutes, twice as often as with
  the times shifted by ±1 hour (a dense small load is „together” with everything by chance). The survivor is the
  user-named one, else the plug-linked one, else the one with more events; the absorbed signature's events and all its
  runs move to it. An exclusive signature takes part only with another signature of the same plug, both with events the plug
  confirmed. A merge by hand stays possible: the events the user brings in take the user's label with the appliance's name,
  so the next re-clustering does not undo it; what the signature caught before is not confirmed by that merge. Being
  the user's labels, those events train the classifier, where they can form a class with the appliance's name; a prediction of
  it counts as no label (above).
- **The „sheet”** of a signature (ΔP mean ± deviation, ΔQ, power factor, transient or „unknown n of m”, typical run
  length with percentiles, a 24-bar histogram of hours, plug share, energy over 7 days) is computed on demand from the
  events and runs, by the same function that feeds the automatic merge, so what the user sees is what the rule used.
  It also shows how the runs repeat over the last 30 days: typical duration, the interval between switch-ons (pauses
  over 6 h left out) and sessions (runs at most 120 s apart, from an end to the next start: how many, pulses and energy
  per session). This part is information only; no rule reads it.

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

**The runs of a plug with a single appliance come from the plug** (section 2), not from events: on → off of its transitions, the
power is the median level, the energy the integral of the plug's power. They live on one signature of the plug, the one with the
most confirmed events, then the lowest id (a new one when it has none), and carry no event (`StartEventId` 0; the column has no foreign
key, so there is no migration and no false row in `MeterEvents`). Its events open and pair nothing, but a switch-off still counts for
the house level of the others' runs. A run has no 6-hour limit, since the plug sees its end; if the data stop (no row for more
than 70 s) it closes at the last second with data as uncertain (`Timeout`), with the energy measured so far.

A rebuild (a regrouping, a merge, a start) replays the stored readings, deletes what stands from there on and writes the same runs
again; nothing older than the plug's first data goes, since nothing could rebuild it, so old runs from events stay there. Live, every
10 s the seconds that arrived are fed to the same timeline, 30 s behind now because the stream's rows are written every 10 s. A row
that comes more than 30 s late (writes of the stream failing twice in a row) is past the timeline, so only the next rebuild sees it.
Those signatures stay measured by the plug, so these runs never enter the detected energy, „Acum pornite”,
`kasa_appliance_*` or the „Consumatori” section of `/history`. Their row on `/consumatori` shows, from these runs and over the same window,
the switch-ons, the mean level, the duration and the repetition, calculated when the page asks; `Count` stays the number of
events the signature holds, the confirmed ones. Its sheet shows the runs; its list of events, what was confirmed.

## 6. Duration: telling apart what ΔP and ΔQ cannot

An espresso machine (pulses of ≈ 1.43 kW for 10–20 s, repeated after tens of seconds) and a kettle (≈ 1.45 kW continuous
for 1–7 minutes) have the same step, so they fall into one signature. What differs is how long a run lasts.

- A signature can have a **duration band** (minimum and maximum seconds, inclusive, either open). It is set only
  by hand, on the „fișa” or through the API; it is never inferred.
- **Moving a run.** When a run closes with its own switch-off (never one closed by the house level or by the 6-hour
  limit) and its duration is out of the band of its signature S, it moves when exactly one other signature T qualifies:
  not hidden, a band that holds the duration, no plug or the plug of S, and |ΔP| and ΔQ of the switch-on within the anchor
  radius (1.5 × eps) of T, or of S when one was split from the other (their centres can then be the same). An exclusive T
  (section 2) qualifies only when its plug confirmed both events of the run. Two
  candidates, or none, leave it where it is. The run and both events move, and with the run its energy, day by day. An
  event labelled by the user holds the whole run in place: moving only the other event would break the pair at the next
  rebuild. The rule also runs when the runs are rebuilt, because the anchors may give S a run that does not fit it. A run from a
  plug (section 5) has no events to move, so a band does not act on it.
- **The source `DurationRule`.** The moved events take this source and T's name as their label. The re-clustering treats
  them as labelled: it never moves them, and the centres include them.
- **Splitting.** „Împarte după durată” (`POST …/split-by-duration`, a dry run unless told otherwise) sends the paired runs
  of S up to a limit, of every stored day, and their events to the signature with the given name if there is a
  compatible one (a band of its own must start at zero and end at the limit or below it: a band of 50–100 s is another
  component), otherwise to a new one named by the user. T ends at the limit, S starts there unless it has a band of
  its own. Signatures and moves are written in one transaction. If S is measured by a plug so is T, even if the plug cannot label its pulses (section 7): a split moves
  energy between two names and must not take it out of the plug's side of the balance. A signature of a plug with a single appliance
  has no run on events since 0.13.2, so its split finds nothing to move. Where two sides of one exist from before, the events the
  plug confirmed keep its label and stay at the re-clustering, those it did not confirm leave at the next one, and all the runs
  of the plug live on one of them. New pulses the plug cannot label go to neither side.
- **Waiting in one place.** Two sides of a split can have the same centre, so „the nearest” would give a switch-on to one
  and its switch-off to the other, and the run would never pair. Every new event near such a pair therefore goes to S; the
  run's duration sends it across when it closes. So a pulse still running counts under S („Acum pornite”,
  `kasa_appliance_power_w`) until it closes and moves. An event the plug did not confirm does not wait in an exclusive S.
  This holds only for a split: two bands set by hand on signatures
  with the same centre have no such place, a switch-on and its switch-off can land apart and the run then never closes
  as paired, so for two appliances with the same centre use the split.
- **Merging.** Two signatures with disjoint bands are never merged, whatever the rule; two bands that only touch (the
  two sides of a split) count as disjoint. A survivor without a band takes the absorbed one's. Uniting the two sides by
  hand („Unește cu…”) undoes the split: the result holds both kinds of runs, so it has no band and no link between them.
- **Classifier.** Its features do not change, and the moved events are not among its examples: ML.NET does not see the
  duration, so two classes with the same features would only lower its accuracy.

## 7. What it cannot detect — known limits

- **No steady state, no event.** Appliances with a continuously variable power (induction hobs, inverter compressors
  and heat pumps, dimmers) do not settle within 30 s, so they produce no event or many small ones.
- **Slow ramps.** Slow drift moves the level without an event; a ramp faster than ≈ 3 W/s on a low base can be seen
  as a step.
- **Short pulses.** The plug matcher compares *stabilised levels* (medians before and after). A pulse shorter than
  about 15 s returns to the baseline, so the difference of medians is near zero and the plug does not label it,
  although the plug saw the peak. On a plug with a single appliance such a pulse stays out of the plug's signature
  (section 2) and is counted with the unknown ones; its transitions still count if it holds 5 s.
- **A false shadow.** A shadow is decided by time and size alone. A small foreign step that happens to be the only candidate of a
  transition leaves „detected” with it, so its energy stays in the rest of the house. Estimated from false moments on the real data:
  about 15 % of the shadows. The plug's own energy and runs are not affected.
- **A plug's level is not a meter's step.** The row of a single-appliance plug shows the median level of its runs (≈ 66 W on the
  measured fridge), which is larger than the median step the plug's windows see (+44 W / −57 W: ramps and the compressor's settling).
- **Small loads, 15–50 W.** A UPS, lamps, chargers and a PC have nearly the same ΔP, ΔQ and transient. Their
  signatures stay mixed; lowering the threshold to 15 W gave 76 events in 16 minutes, mostly from a UPS. The meter itself
  moves by a median of 17 W per second.
- **Simultaneous switching.** Two appliances switched within 2 s in opposite directions give one net step; two
  large steps less than ≈ 7 s apart, or two plugs moving together, give no plug label.
- **Similar power, different shape.** An espresso machine (pulses of ≈ 1.4 kW) and a kettle (a continuous ≈ 1.5 kW) look alike
  by ΔP, ΔQ and transient. A duration band separates them once it has been set by hand (section 6). It cannot separate
  runs that overlap (a kettle and an espresso machine at once pair first in, first out, so the durations are wrong),
  nor two appliances whose durations overlap. A wobble of the meter just before a step can also start the run up to
  a few seconds early, which is why a limit should not sit close to the durations it separates.
- **ΔQ is a whole-house difference.** A capacitive load (a switched-mode supply) lowers |Q| in an inductive house and
  raises it in a house with Q ≈ 0, so its sign depends on what else is running; this is why the eps for ΔQ is wide.
- **Multi-stage appliances** appear as several signatures (one per component); the pairing assumes one switch-off per
  switch-on.
- **Plug offline** means no label for that period, and for a plug with a single appliance no transition: a run in progress ends as
  uncertain, and the state starts again from the first reading, without a run until the next switch-on.

## 8. Open ideas

- **Detection on the rest.** Run an event detector on *meter − Σ plugs* instead of on the meter, so the measured
  appliances stop masking the unmeasured ones, and so that smaller agitated loads (a massage chair, measured at ≈ +50 W
  on average with no step ≥ 100 W) become visible.
- **Duration as a feature.** Done in 0.13.0 as an explicit band, after the run closes (section 6), not as a distance
  term when matching an anchor. Inferring the band from the sheet remains open.
- **A median before the detector.** A 3-second median on the power that sets the levels was measured in 0.13.0 on 63 hours
  of the real meter with `tools/KasaMonitor.NilmReplay` and **not integrated**: 16 % more events, 47 % more on/off flickers within 5 s, 38
  steps of ≥ 100 W lost and 28 % of the common events moved by a second or more (figures in
  [decisions.md](decisions.md), 0.13.0). What it did better is worth another look: on a measured espresso cycle it found
  three pump pulses the detector misses, and a pulse that a wobble made start 7 s early began on its own second.
- **Peak-based plug matching for short events** (a window around the event looking for a peak, not for a changed
  level), to label the pulses described above.
