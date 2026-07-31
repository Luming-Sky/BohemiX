using System.Threading;
using System.Threading.Tasks;

namespace BohemiX.App.Services;

public sealed record AvatarCropRequest(
    string SourcePath,
    double Zoom,
    double HorizontalOffset,
    double VerticalOffset);

public interface IAvatarCropService
{
    Task<string> CreateSquareAvatarAsync(
        AvatarCropRequest request,
        CancellationToken cancellationToken = default);
}
