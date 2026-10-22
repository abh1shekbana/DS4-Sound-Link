using ControllerAudioSwitcher.Core.Models;

namespace ControllerAudioSwitcher.Core.Interfaces;

public interface ISettingsService
{
    AppSettings CurrentSettings { get; }
    void Save();
    void Load();
}
