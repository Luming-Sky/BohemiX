using System.Text.Json.Serialization;
using BohemiX.Core.Models;

namespace BohemiX.Infrastructure.Services;

[JsonSerializable(typeof(GameNewsFeed))]
internal sealed partial class GameNewsJsonContext : JsonSerializerContext;
