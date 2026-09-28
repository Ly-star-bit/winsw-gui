using System;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Which of the console's own failures get a dialog. The failure this guards against is a
    /// status poll that fails every two seconds and used to stack a dialog on a dialog, forever,
    /// in a tray session nobody was looking at.
    /// </summary>
    public class ErrorDialogGateTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void OnlyOneDialogIsOpenAtATimeAndTheOthersAreCounted()
        {
            var gate = new ErrorDialogGate();

            Assert.Equal(0, gate.TryOpen("poll", T0));
            Assert.Null(gate.TryOpen("other", T0.AddSeconds(1)));
            Assert.Null(gate.TryOpen("third", T0.AddSeconds(2)));
            Assert.False(gate.TryOpenNotice());

            Assert.Equal(2, gate.Close(T0.AddSeconds(30)));

            Assert.Equal(0, gate.TryOpen("new", T0.AddSeconds(31)));
        }

        /// <summary>
        /// The count is given in a notice of its own, which holds the screen like any dialog;
        /// what arrives under it is counted for the next one.
        /// </summary>
        [Fact]
        public void TheNoticeHoldsTheScreenAndCountsWhatArrivesUnderIt()
        {
            var gate = new ErrorDialogGate();

            Assert.Equal(0, gate.TryOpen("click", T0));
            Assert.Null(gate.TryOpen("poll", T0.AddSeconds(2)));
            Assert.Equal(1, gate.Close(T0.AddSeconds(10)));

            Assert.True(gate.TryOpenNotice());
            Assert.Null(gate.TryOpen("rescan", T0.AddSeconds(12)));
            Assert.Equal(1, gate.Close(T0.AddSeconds(15)));

            Assert.True(gate.TryOpenNotice());
            Assert.Equal(0, gate.Close(T0.AddSeconds(16)));
        }

        /// <summary>
        /// A failure that came back while its own dialog was open is not shown again for a
        /// while; its repeats are counted, and the count is in the dialog that follows.
        /// </summary>
        [Fact]
        public void AFailureThatRecursByItselfIsKeptQuietThenShownWithItsCount()
        {
            var gate = new ErrorDialogGate();
            var dismissed = T0.AddSeconds(30);

            Assert.Equal(0, gate.TryOpen("poll", T0));
            Assert.Null(gate.TryOpen("poll", T0.AddSeconds(2)));
            Assert.Equal(1, gate.Close(dismissed));

            // The notice about it is not held up by it.
            Assert.True(gate.TryOpenNotice());
            Assert.Null(gate.TryOpen("poll", dismissed.AddSeconds(2)));
            Assert.Equal(0, gate.Close(dismissed.AddSeconds(3)));

            Assert.Null(gate.TryOpen("poll", dismissed.AddSeconds(4)));
            Assert.Null(gate.TryOpen("poll", dismissed + ErrorDialogGate.QuietPeriod - TimeSpan.FromSeconds(1)));

            Assert.Equal(3, gate.TryOpen("poll", dismissed + ErrorDialogGate.QuietPeriod));
            Assert.Equal(0, gate.Close(dismissed + ErrorDialogGate.QuietPeriod + TimeSpan.FromSeconds(5)));

            // Shown, and it did not come back under the dialog: the next time is shown again,
            // with nothing left to report.
            Assert.Equal(0, gate.TryOpen("poll", dismissed + ErrorDialogGate.QuietPeriod + TimeSpan.FromSeconds(6)));
        }

        /// <summary>A failure that runs out of quiet and comes straight back under a dialog keeps its count.</summary>
        [Fact]
        public void RepeatsNotYetReportedAreKept()
        {
            var gate = new ErrorDialogGate();

            Assert.Equal(0, gate.TryOpen("poll", T0));
            Assert.Null(gate.TryOpen("poll", T0.AddSeconds(2)));
            Assert.Equal(1, gate.Close(T0.AddSeconds(3)));
            Assert.Null(gate.TryOpen("poll", T0.AddSeconds(5)));

            var later = T0.AddSeconds(3) + ErrorDialogGate.QuietPeriod;
            Assert.Equal(0, gate.TryOpen("click", later));
            Assert.Null(gate.TryOpen("poll", later.AddSeconds(1)));
            Assert.Equal(1, gate.Close(later.AddSeconds(2)));

            var end = later.AddSeconds(2) + ErrorDialogGate.QuietPeriod;
            Assert.Equal(1, gate.TryOpen("poll", end));
        }

        /// <summary>
        /// A failure somebody causes by clicking never arrives under its own dialog, so each
        /// click that fails is answered.
        /// </summary>
        [Fact]
        public void AFailureCausedByAClickIsAnsweredEveryTime()
        {
            var gate = new ErrorDialogGate();

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(0, gate.TryOpen("start button", T0.AddSeconds(10 * i)));
                Assert.Equal(0, gate.Close(T0.AddSeconds((10 * i) + 5)));
            }
        }

        /// <summary>
        /// A click can start an operation that fails later, under a dialog about something
        /// else. It is counted there like any other, but the next click is still answered.
        /// </summary>
        [Fact]
        public void ACommandThatFailsUnderADialogIsCountedButNeverKeptQuiet()
        {
            var gate = new ErrorDialogGate();

            Assert.Equal(0, gate.TryOpen("poll", T0));
            Assert.Null(gate.TryOpen("start", T0.AddSeconds(2), command: true));
            Assert.Equal(1, gate.Close(T0.AddSeconds(3)));

            Assert.Equal(0, gate.TryOpen("start", T0.AddSeconds(10), command: true));
            Assert.Equal(0, gate.Close(T0.AddSeconds(11)));
        }

        [Fact]
        public void AnotherFailureIsShownWhileOneIsKeptQuiet()
        {
            var gate = new ErrorDialogGate();

            Assert.Equal(0, gate.TryOpen("poll", T0));
            Assert.Null(gate.TryOpen("poll", T0.AddSeconds(2)));
            Assert.Equal(1, gate.Close(T0.AddSeconds(3)));

            Assert.Equal(0, gate.TryOpen("save", T0.AddSeconds(10)));
        }

        /// <summary>
        /// Failures from one place are one failure, whatever their messages say; the cause
        /// under a wrapper is what counts.
        /// </summary>
        [Fact]
        public void TheSameFailureIsKnownByItsTypeAndWhereItWasThrown()
        {
            var one = ThrownHere("C:\\services\\a\\a.xml is locked");
            var two = ThrownHere("C:\\services\\b\\b.xml is locked");
            var elsewhere = ThrownElsewhere("C:\\services\\a\\a.xml is locked");

            Assert.Equal(ErrorDialogGate.SignatureOf(one), ErrorDialogGate.SignatureOf(two));
            Assert.NotEqual(ErrorDialogGate.SignatureOf(one), ErrorDialogGate.SignatureOf(elsewhere));
            Assert.NotEqual(ErrorDialogGate.SignatureOf(one), ErrorDialogGate.SignatureOf(new ArgumentException("x")));
            Assert.Equal(
                ErrorDialogGate.SignatureOf(one),
                ErrorDialogGate.SignatureOf(new InvalidOperationException("wrapped", one)));
            Assert.Equal(string.Empty, ErrorDialogGate.SignatureOf(null));
        }

        private static Exception ThrownHere(string message)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException e)
            {
                return e;
            }
        }

        private static Exception ThrownElsewhere(string message)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException e)
            {
                return e;
            }
        }
    }
}
