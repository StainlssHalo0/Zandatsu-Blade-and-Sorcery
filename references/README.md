# Local reference assemblies

Game and Unity DLLs are intentionally not tracked or redistributed by this repository.

If you build with `-p:GameDir="C:\path\to\Blade & Sorcery"`, the project finds these files automatically and you do not need to copy anything here.

Otherwise, copy the files below from your own Blade & Sorcery 1.3.2 installation:

```text
BladeAndSorcery_Data\Managed
```

into this `references` folder.

## Required for Zandatsu.dll

```text
ThunderRoad.dll
ThunderRoad.Manikin.dll
Unity.Addressables.dll
Unity.ResourceManager.dll
UnityEngine.dll
UnityEngine.AssetBundleModule.dll
UnityEngine.CoreModule.dll
UnityEngine.ImageConversionModule.dll
UnityEngine.InputLegacyModule.dll
UnityEngine.PhysicsModule.dll
```

## Additionally required for the optional ZandatsuLab test bench

```text
ThunderRoad.MaterialInstance.dll
UnityEngine.ScreenCaptureModule.dll
```

These DLLs are compile-time references only. Do not copy them into the mod's release folder. At runtime, the game supplies its own assemblies.
