namespace Fishbowl.Core.Plugins;

// IBotClient.SendAsync couldn't deliver (not connected, no channel, the DM
// can't be opened). Senders treat it like any failure: nothing is latched,
// so the message can go out later.
public sealed class BotDeliveryException : Exception
{
    public BotDeliveryException(string message) : base(message) { }
}
