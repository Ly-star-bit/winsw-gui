using System;
using System.IO;
using System.Threading.Tasks;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The staged half of an elevated save. The copy itself needs an elevation prompt, so the
    /// tests supply their own and check what is left behind on either side of it.
    /// </summary>
    public class StagedWriteTests
    {
        [Fact]
        public async Task TheStagedCopyIsGoneAfterASuccessfulWrite()
        {
            string? staged = null;

            var result = await StagedWrite.ElevatedAsync(Model(), @"C:\somewhere\demo.xml", (from, _) =>
            {
                staged = from;
                Assert.True(File.Exists(from), "the staged file should exist while it is being copied");
                return Task.FromResult(CommandResult.Ok());
            });

            Assert.True(result.Succeeded);
            Assert.NotNull(staged);
            Assert.False(File.Exists(staged!), "the staged file should not outlive the save");
        }

        /// <summary>
        /// The staged file carries whatever the configuration does, a service account's
        /// password included. It must not survive a save that failed either.
        /// </summary>
        [Fact]
        public async Task TheStagedCopyIsGoneAfterAFailedWrite()
        {
            string? staged = null;

            await StagedWrite.ElevatedAsync(Model(), @"C:\somewhere\demo.xml", (from, _) =>
            {
                staged = from;
                return Task.FromResult(CommandResult.Failed("no"));
            });

            Assert.False(File.Exists(staged!));
        }

        [Fact]
        public async Task TheStagedCopyIsGoneEvenIfTheCopyThrows()
        {
            string? staged = null;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                StagedWrite.ElevatedAsync(Model(), @"C:\somewhere\demo.xml", (from, _) =>
                {
                    staged = from;
                    throw new InvalidOperationException("boom");
                }));

            Assert.False(File.Exists(staged!));
        }

        /// <summary>
        /// Two configurations that happen to share a file name used to stage over one another,
        /// because the staged name was the configuration's own.
        /// </summary>
        [Fact]
        public async Task TwoSavesOfTheSameNameDoNotShareAStagedFile()
        {
            string? first = null;
            string? second = null;

            await StagedWrite.ElevatedAsync(Model(), @"C:\a\demo.xml", (from, _) =>
            {
                first = from;
                return Task.FromResult(CommandResult.Ok());
            });

            await StagedWrite.ElevatedAsync(Model(), @"C:\b\demo.xml", (from, _) =>
            {
                second = from;
                return Task.FromResult(CommandResult.Ok());
            });

            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task TheStagedNameIsNotTheDestinationName()
        {
            string? staged = null;

            await StagedWrite.ElevatedAsync(Model(), @"C:\somewhere\demo.xml", (from, _) =>
            {
                staged = from;
                return Task.FromResult(CommandResult.Ok());
            });

            Assert.DoesNotContain("demo", Path.GetFileName(staged!), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Save points the model at what it wrote. Left pointing at the staged copy, the
        /// editor would resolve its relative paths against the temporary directory — and that
        /// copy is deleted on the way out, so the model would be naming a file that is gone.
        /// </summary>
        [Fact]
        public async Task TheModelStillNamesTheRealFileAfterwards()
        {
            var model = Model();
            model.FilePath = @"C:\somewhere\demo.xml";

            await StagedWrite.ElevatedAsync(model, @"C:\somewhere\demo.xml", (_, _) =>
                Task.FromResult(CommandResult.Ok()));

            Assert.Equal(@"C:\somewhere\demo.xml", model.FilePath);
        }

        [Fact]
        public async Task AModelThatNamedNoFileStillNamesNone()
        {
            var model = Model();
            Assert.Null(model.FilePath);

            await StagedWrite.ElevatedAsync(model, @"C:\somewhere\demo.xml", (_, _) =>
                Task.FromResult(CommandResult.Ok()));

            Assert.Null(model.FilePath);
        }

        [Fact]
        public async Task WhatIsStagedIsTheConfiguration()
        {
            string staged = string.Empty;

            await StagedWrite.ElevatedAsync(Model(), @"C:\somewhere\demo.xml", (from, _) =>
            {
                staged = File.ReadAllText(from);
                return Task.FromResult(CommandResult.Ok());
            });

            Assert.Contains("<id>demo</id>", staged, StringComparison.Ordinal);
            Assert.Contains("demo.exe", staged, StringComparison.Ordinal);
        }

        private static ServiceConfigModel Model()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.DisplayName = "Demo";
            model.Executable = @"C:\app\demo.exe";
            return model;
        }
    }
}
