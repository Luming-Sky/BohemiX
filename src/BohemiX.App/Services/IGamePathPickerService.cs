using System.Threading.Tasks;

namespace BohemiX.App.Services;

public interface IGamePathPickerService
{
    Task<string?> PickGameExecutableAsync();

    Task<string?> PickGameDirectoryAsync();

    Task<string?> PickSaveDirectoryAsync();

    Task<string?> PickModPackageAsync();

    Task<string?> PickModPackAsync();

    Task<string?> PickAvatarAsync();

    Task<string?> PickAppearanceMediaAsync(bool animated);
}
