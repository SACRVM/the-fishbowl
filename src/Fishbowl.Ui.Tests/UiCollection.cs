namespace Fishbowl.Ui.Tests;

/// <summary>
/// Every UI test class shares ONE Fishbowl.Host subprocess. Per-class
/// fixtures meant one host per class, started in parallel against the same
/// fishbowl-data/ directory — two processes migrating the same SQLite files
/// and fetching the embedding model into the same place. On a cold CI runner
/// the second host never came up. One collection also runs the classes one
/// after another, so their notes don't race either.
/// </summary>
[CollectionDefinition(Name)]
public class UiCollection : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "ui";
}
