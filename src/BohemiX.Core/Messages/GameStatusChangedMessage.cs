namespace BohemiX.Core.Messages;

public sealed record GameStatusChangedMessage(string Status, int? ProcessId);

