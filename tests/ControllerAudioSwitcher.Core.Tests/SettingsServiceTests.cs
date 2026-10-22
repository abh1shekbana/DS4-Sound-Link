using ControllerAudioSwitcher.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ControllerAudioSwitcher.Core.Tests;

[TestClass]
public class SettingsServiceTests
{
    private string _tempFilePath = null!;
    private DiagnosticService _diagnosticService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json");
        _diagnosticService = new DiagnosticService();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    [TestMethod]
    public void SettingsService_Defaults_AreValid()
    {
        var service = new SettingsService(_diagnosticService, _tempFilePath);
        var settings = service.CurrentSettings;

        Assert.IsNotNull(settings);
        Assert.IsTrue(settings.AutoStartOnConnect);
        Assert.AreEqual((byte)110, settings.HeadphoneVolume);
        Assert.IsTrue(settings.HeadsetMode);
    }

    [TestMethod]
    public void SettingsService_SaveAndLoad_PreservesValues()
    {
        var service1 = new SettingsService(_diagnosticService, _tempFilePath);
        service1.CurrentSettings.AutoStartOnConnect = false;
        service1.CurrentSettings.HeadphoneVolume = 85;
        service1.CurrentSettings.SelectedAudioDeviceId = "mock-device-id-123";
        service1.Save();

        Assert.IsTrue(File.Exists(_tempFilePath));

        var service2 = new SettingsService(_diagnosticService, _tempFilePath);
        Assert.IsFalse(service2.CurrentSettings.AutoStartOnConnect);
        Assert.AreEqual((byte)85, service2.CurrentSettings.HeadphoneVolume);
        Assert.AreEqual("mock-device-id-123", service2.CurrentSettings.SelectedAudioDeviceId);
    }
}
