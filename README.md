# Blish-HUD-Home-Tab

A configurable Home tab for Blish HUD with four quadrants.

Each quadrant can be assigned a block from the module settings:

- Latest News
- GW2 News
- Dailies
- Module Updates
- New Modules
- None

Assignments are unique and apply immediately. The default layout matches the original Home tab layout.

## External blocks

Other Blish modules can provide Home tab blocks through `HomeTab.Contracts.dll`.

Reference `HomeTab.Contracts.dll` from your module and declare a dependency on Home Tab in your module manifest.

Implement `entrhopi.HomeTab.Contracts.IHomeTabBlock`:

```csharp
public sealed class MyBlock : IHomeTabBlock
{
    public string Id => "my.module.my-block";
    public string DisplayName => "My Block";

    public Control CreateContent()
    {
        return new Panel(); // Add your controls here.
    }
}
```

Implement and register a provider when your module loads:

```csharp
public sealed class MyProvider : IHomeTabBlockProvider
{
    public string ProviderId => "my.module";

    public IEnumerable<IHomeTabBlock> GetBlocks()
    {
        yield return new MyBlock();
    }
}

// OnModuleLoaded
HomeTabBlockRegistry.Register(this.provider);

// Unload
HomeTabBlockRegistry.Unregister(this.provider);
```

Block IDs must be stable and unique. Prefix them with your module namespace. The Home tab renders the quadrant header using `DisplayName` and gives the returned control the full content area.
