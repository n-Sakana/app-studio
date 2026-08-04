namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // What this session amounts to, worked out once and read by everything that
    // has to say it: the window, the report and the assistant handover.
    //
    // It exists so the three cannot disagree. A screen that says "12 actions"
    // while the report says "9 recorded, 3 unusable" is two answers to one
    // question, and the reader has no way to tell which is the true one.
    public sealed class SessionVerdict
    {
        public const string StateOk = "ok";
        public const string StatePartial = "partial";
        public const string StateFailed = "failed";
        public const string StateEmpty = "empty";

        public string State = StateEmpty;
        public string Kind;
        public int Steps;
        public int Screens;
        public int Shots;
        public int Elements;
        public int Apps;
        public int Limits;
        public int InputEvents;
        // How many of the recorded actions could be carried out again, and how
        // many could not because nothing identifies what they acted on.
        public int Replayable;
        public int NotReplayable;
        public bool HasReplay;
        public int ReplayDone;
        public int ReplayStopped;
        public string ReplayStopReason;
        // Short lines, each already carrying its own count. These are the
        // warnings a reader has to see before deciding anything; the full text
        // of every limit stays on the detail side.
        public List<string> Warnings = new List<string>();

        public bool IsRecording { get { return String.Equals(Kind, StudioSession.KindRecord, StringComparison.Ordinal); } }

        public static SessionVerdict Of(StudioSession session)
        {
            SessionVerdict verdict = new SessionVerdict();
            if (session == null) return verdict;
            verdict.Kind = session.Kind;
            verdict.Steps = session.Steps.Count;
            verdict.Screens = session.Screens.Screens.Count;
            verdict.Shots = session.Screens.ShotCount;
            verdict.Elements = session.Elements.Count;
            verdict.Apps = session.Apps.Count;
            verdict.Limits = session.Limits.Count;
            verdict.InputEvents = session.InputEvents.Count;

            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (CanReplay(step)) verdict.Replayable++;
                else verdict.NotReplayable++;
                if (step.LastReplay == null) continue;
                verdict.HasReplay = true;
                if (String.Equals(step.LastReplay.State, "done", StringComparison.Ordinal)) verdict.ReplayDone++;
                else
                {
                    verdict.ReplayStopped++;
                    // Only which step stopped. Why it stopped is a sentence,
                    // and a sentence does not belong in a line that is read at
                    // a glance - it is in the replay table, one fold away.
                    if (verdict.ReplayStopReason == null) verdict.ReplayStopReason = step.StepId;
                }
            }

            int missingShots = verdict.Screens - verdict.Shots;
            if (missingShots > 0) verdict.Warnings.Add(Count("verdict-warn-shots.txt", "screen(s) have no picture", missingShots));
            if (verdict.NotReplayable > 0) verdict.Warnings.Add(Count("verdict-warn-locator.txt", "action(s) cannot be replayed: nothing identifies what they acted on", verdict.NotReplayable));
            if (verdict.Limits > 0) verdict.Warnings.Add(Count("verdict-warn-limits.txt", "thing(s) could not be obtained", verdict.Limits));
            if (Acquire.PublishesNoStructure(session)) verdict.Warnings.Add(Messages.Text("verdict-warn-nostructure.txt", "This application publishes no structure, so the pictures are the only description of its surface."));
            if (session.Screens.Screens.Count == 0) verdict.Warnings.Add(Messages.Text("verdict-warn-noscreen.txt", "No screen was acquired."));

            verdict.State = Decide(session, verdict);
            return verdict;
        }

        private static string Decide(StudioSession session, SessionVerdict verdict)
        {
            bool recording = verdict.IsRecording;
            if (recording && verdict.Steps == 0) return StateEmpty;
            if (!recording && verdict.Screens == 0) return StateFailed;
            if (!recording && verdict.Elements == 0) return StateFailed;
            if (verdict.HasReplay && verdict.ReplayStopped > 0) return StatePartial;
            if (verdict.NotReplayable > 0 || verdict.Limits > 0 || verdict.Screens > verdict.Shots) return StatePartial;
            return StateOk;
        }

        // One sentence with a subject and a result. Nothing here says "done" on
        // its own: what was done, to how much, and what is missing are in the
        // same line.
        public string Headline
        {
            get
            {
                if (State == StateEmpty) return Messages.Text("verdict-empty.txt", "Nothing was recorded.");
                if (State == StateFailed)
                {
                    return IsRecording
                        ? Messages.Text("verdict-failed-record.txt", "The recording produced nothing that can be read or replayed.")
                        : Messages.Text("verdict-failed-snap.txt", "The window could not be taken apart: nothing inside it was obtained.");
                }
                string head = IsRecording
                    ? Count("verdict-record-head.txt", "action(s) recorded", Steps)
                    : Count("verdict-snap-head.txt", "element(s) obtained", Elements);
                if (State == StateOk) return head + Messages.Text("verdict-ok-tail.txt", ", with nothing missing that any layer noticed.");
                if (NotReplayable > 0)
                {
                    return head + Messages.Text("verdict-partial-join.txt", ", but ") +
                        Count("verdict-partial-locator.txt", "of them cannot be replayed", NotReplayable) + Stop();
                }
                if (HasReplay && ReplayStopped > 0)
                {
                    return head + Messages.Text("verdict-partial-join.txt", ", but ") +
                        Count("verdict-partial-replay.txt", "of them stopped during replay", ReplayStopped) + Stop();
                }
                return head + Messages.Text("verdict-partial-join.txt", ", but ") +
                    Count("verdict-partial-limits.txt", "thing(s) could not be obtained", Limits + (Screens - Shots)) + Stop();
            }
        }

        // The one move that follows from the state above. There is exactly one,
        // because a screen that offers six equal choices has not told the reader
        // what to do.
        public string NextAction
        {
            get
            {
                if (State == StateEmpty) return Messages.Text("verdict-next-empty.txt", "Record again and do the work you want captured.");
                if (State == StateFailed) return Messages.Text("verdict-next-failed.txt", "Read what could not be obtained, then try the other acquisition route.");
                if (IsRecording && Replayable > 0 && !HasReplay) return Messages.Text("verdict-next-replay.txt", "Replay it on the real application to prove it carries out.");
                if (HasReplay && ReplayStopped > 0) return Messages.Text("verdict-next-stopped.txt", "Read why replay stopped, fix that step, then run it again.");
                return Messages.Text("verdict-next-read.txt", "Open the report, or hand the two files to an assistant.");
            }
        }

        public string StateWord
        {
            get
            {
                if (State == StateOk) return Messages.Text("state-ok.txt", "complete");
                if (State == StatePartial) return Messages.Text("state-partial.txt", "partly complete");
                if (State == StateFailed) return Messages.Text("state-failed.txt", "failed");
                return Messages.Text("state-empty.txt", "nothing recorded");
            }
        }

        public string ReplayLine
        {
            get
            {
                if (Steps == 0) return Messages.Text("verdict-replay-none.txt", "There is nothing to replay.");
                if (!HasReplay)
                {
                    return Replayable.ToString(CultureInfo.InvariantCulture) + " / " + Steps.ToString(CultureInfo.InvariantCulture) +
                        " " + Messages.Text("verdict-replay-ready.txt", "of the recorded actions can be carried out again");
                }
                return ReplayDone.ToString(CultureInfo.InvariantCulture) + " / " +
                    (ReplayDone + ReplayStopped).ToString(CultureInfo.InvariantCulture) + " " +
                    Messages.Text("verdict-replay-done.txt", "carried out again") +
                    (ReplayStopped > 0
                        ? ", " + Count("verdict-replay-stopped.txt", "stopped", ReplayStopped) +
                          (ReplayStopReason == null ? "" : " (" + ReplayStopReason + ")")
                        : "");
            }
        }

        // A step can be carried out again when something identifies what it
        // acted on. Position is not identity: a step that only knows where it
        // was is counted as not replayable rather than being allowed to press
        // an unknown part of somebody's application.
        public static bool CanReplay(StepRecord step)
        {
            if (step == null) return false;
            if (step.Kind == StepRecord.KindAppSwitch) return !String.IsNullOrEmpty(step.WindowClass) || !String.IsNullOrEmpty(step.AppName);
            if (step.Kind == StepRecord.KindKeyChord) return !String.IsNullOrEmpty(step.KeyChord);
            if (!Identifies(step.Locators)) return false;
            if (step.Kind == StepRecord.KindDrag) return Identifies(step.DropLocators);
            return true;
        }

        private static bool Identifies(List<ElementLocator> locators)
        {
            if (locators == null) return false;
            for (int index = 0; index < locators.Count; index++)
            {
                if (LocatorResolver.Identifies(locators[index].Strategy)) return true;
            }
            return false;
        }

        private static string Stop()
        {
            return Messages.Text("verdict-stop.txt", ".");
        }

        private static string Count(string name, string fallback, int value)
        {
            return value.ToString(CultureInfo.InvariantCulture) + " " + Messages.Text(name, fallback);
        }
    }
}
