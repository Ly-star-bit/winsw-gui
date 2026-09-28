using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Removing the last failure row, the obvious way out of a restart loop, has to reach Windows.
    /// </summary>
    /// <remarks>
    /// A file with no <c>&lt;onfailure&gt;</c> tells the wrapper to leave the service's recovery
    /// alone, so a file whose rows were deleted used to apply as "keep restarting" while the
    /// console said it had been updated. It now writes <c>&lt;onfailure action="none"/&gt;</c>,
    /// which every wrapper applies; a file that never had rows still writes nothing, so recovery
    /// set by hand in services.msc survives Save &amp; apply.
    /// </remarks>
    public class FailureActionClearingTests : IDisposable
    {
        private const string Restarting = @"<service>
  <id>demo</id>
  <executable>demo.exe</executable>
  <!-- recovery -->
  <onfailure action=""restart"" delay=""10 sec"" />
  <onfailure action=""restart"" delay=""1 min"" />
  <resetfailure>1 hour</resetfailure>
  <logpath>%BASE%\logs</logpath>
</service>";

        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N"));

        public FailureActionClearingTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>Load, remove every row, save: the file says "none", and reads back as that row.</summary>
        [Fact]
        public void RemovingTheLastRowWritesNone()
        {
            string path = this.Write(Restarting);

            var model = ServiceConfigModel.Load(path);
            Assert.True(model.DeclaredFailureActions);

            model.FailureActions.RemoveAt(1);
            model.FailureActions.RemoveAt(0);
            model.Save(path);

            string text = File.ReadAllText(path);
            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(text));
            Assert.DoesNotContain("restart", text, StringComparison.Ordinal);

            var again = ServiceConfigModel.Load(path);
            var row = Assert.Single(again.FailureActions);
            Assert.Equal("none", row.Action);
            Assert.Null(row.Delay);
            Assert.True(again.DeclaredFailureActions);
        }

        /// <summary>The preview shows what will be written before anything is saved.</summary>
        [Fact]
        public void ThePreviewSaysNoneAsSoonAsTheLastRowGoes()
        {
            var model = ServiceConfigModel.Load(this.Write(Restarting));

            model.FailureActions.Clear();

            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(model.ToXmlString()));
        }

        /// <summary>Where the rows were, so the comment above them still describes the right block.</summary>
        [Fact]
        public void TheNoneTakesTheRowsPlace()
        {
            var model = ServiceConfigModel.Load(this.Write(Restarting));

            model.FailureActions.Clear();

            string xml = model.ToXmlString();
            int comment = xml.IndexOf("<!-- recovery -->", StringComparison.Ordinal);
            int none = xml.IndexOf(@"<onfailure action=""none"" />", StringComparison.Ordinal);
            int reset = xml.IndexOf("<resetfailure>", StringComparison.Ordinal);
            Assert.True(comment >= 0 && comment < none && none < reset, xml);
        }

        /// <summary>
        /// Written, read back, saved again: it stays "none", and removing that row too keeps it
        /// "none" rather than going back to saying nothing.
        /// </summary>
        [Fact]
        public void NoneRoundTrips()
        {
            string path = this.Write(@"<service><id>demo</id><executable>demo.exe</executable><onfailure action=""none"" /></service>");

            var model = ServiceConfigModel.Load(path);
            model.Description = "changed";
            model.Save(path);
            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(File.ReadAllText(path)));

            model = ServiceConfigModel.Load(path);
            model.FailureActions.Clear();
            model.Save(path);
            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(File.ReadAllText(path)));
        }

        /// <summary>Saving twice from the same model does not stack a second "none".</summary>
        [Fact]
        public void SavingAgainWritesOneNone()
        {
            string path = this.Write(Restarting);
            var model = ServiceConfigModel.Load(path);
            model.FailureActions.Clear();

            model.Save(path);
            model.Save(path);

            Assert.Single(OnFailure(File.ReadAllText(path)));
        }

        /// <summary>
        /// A file that never declared recovery keeps not declaring it: Save &amp; apply must not
        /// clear what somebody set in services.msc.
        /// </summary>
        [Fact]
        public void AFileThatNeverHadRowsStillWritesNothing()
        {
            string path = this.Write("<service><id>demo</id><executable>demo.exe</executable></service>");

            var model = ServiceConfigModel.Load(path);
            Assert.False(model.DeclaredFailureActions);

            // Even after a row has come and gone again.
            model.FailureActions.Add(new FailureAction { Action = "restart", Delay = "10 sec" });
            model.FailureActions.Clear();
            model.Save(path);

            Assert.Empty(OnFailure(File.ReadAllText(path)));
        }

        /// <summary>
        /// A file that started without rows, given one and saved — the service now restarts — and
        /// then cleared and saved again in the same session: the second save has to say "none",
        /// or Windows keeps the restart the first one applied.
        /// </summary>
        [Fact]
        public void RowsAddedAndSavedThenRemovedWriteNone()
        {
            string path = this.Write("<service><id>demo</id><executable>demo.exe</executable></service>");
            var model = ServiceConfigModel.Load(path);

            model.AddFailureAction();
            model.Save(path);
            Assert.Equal(new[] { @"<onfailure action=""restart"" delay=""10 sec"" />" }, OnFailure(File.ReadAllText(path)));
            Assert.True(model.DeclaredFailureActions);

            model.FailureActions.Clear();
            model.Save(path);

            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(File.ReadAllText(path)));
        }

        /// <summary>
        /// Building the preview is not saving: rows that were only ever shown, never written, leave
        /// a file that had none still writing nothing.
        /// </summary>
        [Fact]
        public void RowsOnlyPreviewedDoNotCount()
        {
            string path = this.Write("<service><id>demo</id><executable>demo.exe</executable></service>");
            var model = ServiceConfigModel.Load(path);

            model.AddFailureAction();
            Assert.Single(OnFailure(model.ToXmlString()));
            model.FailureActions.Clear();
            model.Save(path);

            Assert.False(model.DeclaredFailureActions);
            Assert.Empty(OnFailure(File.ReadAllText(path)));
        }

        /// <summary>
        /// Adding a row to a file whose rows were removed: the "none" read back is replaced, not
        /// followed. Behind it, the restart would come only at the second failure.
        /// </summary>
        [Fact]
        public void AddingARowReplacesTheNone()
        {
            string path = this.Write(Restarting);
            var model = ServiceConfigModel.Load(path);
            model.FailureActions.Clear();
            model.Save(path);

            model = ServiceConfigModel.Load(path);
            Assert.Equal("none", Assert.Single(model.FailureActions).Action);

            model.AddFailureAction();

            var row = Assert.Single(model.FailureActions);
            Assert.Equal("restart", row.Action);
            Assert.Equal(new[] { @"<onfailure action=""restart"" delay=""10 sec"" />" }, OnFailure(model.ToXmlString()));

            // Still a file that declared rows: removing the new one goes back to "none".
            Assert.True(model.DeclaredFailureActions);
            model.FailureActions.Clear();
            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(model.ToXmlString()));
        }

        /// <summary>Several rows that all say "none" go the same way; any other row is added to.</summary>
        [Fact]
        public void AddingARowAppendsUnlessEveryRowIsNone()
        {
            var model = ServiceConfigModel.FromXml(@"<service><id>demo</id><executable>demo.exe</executable><onfailure action=""none"" /><onfailure action=""none"" delay=""1 min"" /></service>", null);
            model.AddFailureAction();
            Assert.Equal(new[] { "restart" }, model.FailureActions.Select(a => a.Action));

            model.AddFailureAction();
            Assert.Equal(new[] { "restart", "restart" }, model.FailureActions.Select(a => a.Action));

            // A none a restart follows is somebody's choice: wait out the first failure.
            model = ServiceConfigModel.FromXml(@"<service><id>demo</id><executable>demo.exe</executable><onfailure action=""none"" /><onfailure action=""restart"" /></service>", null);
            model.AddFailureAction();
            Assert.Equal(new[] { "none", "restart", "restart" }, model.FailureActions.Select(a => a.Action));

            model = ServiceConfigModel.CreateNew();
            model.AddFailureAction();
            Assert.Equal(new[] { "restart" }, model.FailureActions.Select(a => a.Action));
        }

        /// <summary>The rows go one at a time, so whoever watches the collection is told which went.</summary>
        [Fact]
        public void TheReplacedRowsAreReportedAsRemoved()
        {
            var model = ServiceConfigModel.FromXml(@"<service><id>demo</id><executable>demo.exe</executable><onfailure action=""none"" /></service>", null);
            var none = model.FailureActions[0];
            var removed = new List<object>();
            model.FailureActions.CollectionChanged += (_, e) =>
            {
                Assert.NotEqual(NotifyCollectionChangedAction.Reset, e.Action);
                removed.AddRange(e.OldItems?.Cast<object>() ?? Enumerable.Empty<object>());
            };

            model.AddFailureAction();

            Assert.Same(none, Assert.Single(removed));
        }

        [Fact]
        public void ANewConfigurationWritesNothing()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.Executable = "demo.exe";
            model.FailureActions.Add(new FailureAction());
            model.FailureActions.Clear();

            Assert.False(model.DeclaredFailureActions);
            Assert.Empty(OnFailure(model.ToXmlString()));
        }

        /// <summary>Rows added after the old ones were removed are written as they are, with no "none".</summary>
        [Fact]
        public void NewRowsReplaceTheNone()
        {
            string path = this.Write(Restarting);
            var model = ServiceConfigModel.Load(path);

            model.FailureActions.Clear();
            model.FailureActions.Add(new FailureAction { Action = "restart", Delay = "5 min" });
            model.Save(path);

            Assert.Equal(new[] { @"<onfailure action=""restart"" delay=""5 min"" />" }, OnFailure(File.ReadAllText(path)));
        }

        /// <summary>
        /// Deleting the rows in the raw XML editor is removing them all the same: the model built
        /// from the text keeps knowing the file had them.
        /// </summary>
        [Fact]
        public void RowsDeletedAsXmlTextAreStillWrittenAsNone()
        {
            string path = this.Write(Restarting);
            var loaded = ServiceConfigModel.Load(path);
            string edited = Regex.Replace(loaded.ToXmlString(), @"\s*<onfailure[^>]*/>", string.Empty);

            var fromText = ServiceConfigModel.FromXml(edited, path);
            Assert.False(fromText.DeclaredFailureActions);
            Assert.Empty(OnFailure(fromText.ToXmlString()));

            fromText.KeepDeclaredFailureActions(loaded);
            Assert.True(fromText.DeclaredFailureActions);
            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(fromText.ToXmlString()));
        }

        /// <summary>
        /// The editor's History restore, the way it goes: a version from before the rows were
        /// added, loaded over the file that has them, must not save as "leave recovery alone".
        /// </summary>
        [Fact]
        public void ARestoredVersionFromBeforeTheRowsWritesNone()
        {
            string path = this.Write(Restarting);
            string older = Path.Combine(this.directory, "older.xml");
            File.WriteAllText(older, "<service><id>demo</id><executable>demo.exe</executable></service>");

            var current = ServiceConfigModel.Load(path);
            var restored = ServiceConfigModel.Load(older);
            restored.FilePath = path;
            restored.KeepDeclaredFailureActions(current);
            restored.Save(path);

            Assert.Equal(new[] { @"<onfailure action=""none"" />" }, OnFailure(File.ReadAllText(path)));
        }

        /// <summary>And carrying it over from a file that had none changes nothing.</summary>
        [Fact]
        public void CarryingOverFromAFileWithoutRowsChangesNothing()
        {
            var earlier = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable></service>", null);
            var later = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable></service>", null);

            later.KeepDeclaredFailureActions(earlier);

            Assert.False(later.DeclaredFailureActions);
            Assert.Empty(OnFailure(later.ToXmlString()));
        }

        private static string[] OnFailure(string xml) =>
            Regex.Matches(xml, "<onfailure[^>]*/>").Select(m => m.Value).ToArray();

        private string Write(string xml)
        {
            string path = Path.Combine(this.directory, "service.xml");
            File.WriteAllText(path, xml);
            return path;
        }
    }
}
