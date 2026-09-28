using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The start type in the detail panel's facts, which a Chinese interface used to show as
    /// "Automatic (delayed)": named through the dictionaries, in services.msc's words.
    /// </summary>
    public class StartModeTextTests
    {
        [Theory]
        [InlineData(ServiceStartMode.Automatic, false, "M.StartMode.Automatic")]
        [InlineData(ServiceStartMode.Automatic, true, "M.StartMode.AutomaticDelayed")]
        [InlineData(ServiceStartMode.Manual, false, "M.StartMode.Manual")]
        [InlineData(ServiceStartMode.Disabled, false, "M.StartMode.Disabled")]
        [InlineData(ServiceStartMode.Boot, false, "M.StartMode.Boot")]
        [InlineData(ServiceStartMode.System, false, "M.StartMode.System")]
        [InlineData(null, false, "M.StartMode.Unknown")]
        public void EachStartTypeHasAKeyOfItsOwn(ServiceStartMode? startType, bool delayed, string key)
        {
            Assert.Equal(key, ServiceDiscovery.StartModeKey(startType, delayed));
        }

        /// <summary>
        /// The service control manager keeps the delayed flag whatever the start type, and only
        /// an automatic start is delayed by it.
        /// </summary>
        [Theory]
        [InlineData(ServiceStartMode.Manual)]
        [InlineData(ServiceStartMode.Disabled)]
        [InlineData(null)]
        public void OnlyAnAutomaticStartIsCalledDelayed(ServiceStartMode? startType)
        {
            Assert.Equal(ServiceDiscovery.StartModeKey(startType, false), ServiceDiscovery.StartModeKey(startType, true));
        }

        [Fact]
        public void EveryStartTypeIsNamedInEveryLanguage()
        {
            var keys = StartModeKeys();

            foreach (var language in Localizer.Languages)
            {
                var defined = StringDictionaries.ValuesOf(language.Code);
                var missing = keys.Where(k => !defined.ContainsKey(k)).ToList();
                Assert.True(missing.Count == 0, $"Strings.{language.Code}.xaml is missing: {string.Join(", ", missing)}");
            }
        }

        /// <summary>The point of the change: no English word for a start type outside English.</summary>
        [Fact]
        public void NoOtherLanguageNamesAStartTypeInEnglish()
        {
            var keys = StartModeKeys();

            foreach (var language in Localizer.Languages.Where(l => l.Code != "en"))
            {
                var values = StringDictionaries.ValuesOf(language.Code);
                var english = keys
                    .Where(k => values.TryGetValue(k, out string? text) && text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                    .ToList();

                Assert.True(english.Count == 0, $"Strings.{language.Code}.xaml names these in Latin letters: {string.Join(", ", english)}");
            }
        }

        private static SortedSet<string> StartModeKeys()
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal) { ServiceDiscovery.StartModeKey(null, false) };
            foreach (var startType in Enum.GetValues<ServiceStartMode>())
            {
                keys.Add(ServiceDiscovery.StartModeKey(startType, false));
                keys.Add(ServiceDiscovery.StartModeKey(startType, true));
            }

            Assert.Equal(7, keys.Count);
            return keys;
        }
    }
}
