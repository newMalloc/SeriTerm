using System.IO;
using SeriTerm.App.Services;
using SeriTerm.Core.Send;

namespace SeriTerm.App.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void ValidJsonWithNullMembersCannotCrashStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeriTerm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), """
                { "LastSerial":null, "Presets":[null,{"Name":"bad","Serial":null}],
                  "SearchFavorites":null, "SendHistory":[null], "LogFontSize":100000,
                  "WindowWidth":-1, "TimedSendIntervalSeconds":1e100, "SendDraft":null }
                """);
            var loaded = new JsonSettingsStore(directory).Load();
            Assert.NotNull(loaded.LastSerial);
            Assert.Empty(loaded.Presets);
            Assert.Empty(loaded.SendHistory);
            Assert.Empty(loaded.SearchFavorites);
            Assert.Equal(24, loaded.LogFontSize);
            Assert.Equal(920, loaded.WindowWidth);
            Assert.Equal(1, loaded.TimedSendIntervalSeconds);
            Assert.Equal(string.Empty, loaded.SendDraft);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DraftAndHistorySurviveSettingsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeriTerm-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(directory);
            var history = new SendHistoryEntry("AT\r\n中文", false, LineEnding.None, "UTF-8");
            store.Save(new AppSettings { SendDraft = "draft", SendHistory = [history] });
            var loaded = store.Load();
            Assert.Equal("draft", loaded.SendDraft);
            Assert.Equal(history, Assert.Single(loaded.SendHistory));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
