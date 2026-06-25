# Drokk Storage

Allows multiple players to view storage containers and workbenches simultaneously.

**Author:** drokk
**Version:** 1.0.0
A mod for *7 Days to Die*.

## Install (no compiling needed)

1. Download `DrokkStorage-v1.0.0.zip` from the [latest release](https://github.com/jirish82/DrokkStorage/releases/latest).
2. Extract it into your `7 Days To Die/Mods/` folder so you end up with `Mods/DrokkStorage/`.
3. Launch the game. On multiplayer, the server and all clients need the mod.

## Build from source

You need the .NET SDK and a copy of the game (for the reference assemblies).

```bash
# Point the build at your game install (the folder containing 7DaysToDie_Data/)
dotnet build DrokkStorage.csproj -c Release -p:GamePath="/path/to/7 Days To Die"
```

The compiled `DrokkStorage.dll` plus `ModInfo.xml` and `Config/` make up the deployable mod.

---
*Exported from a private monorepo with `export_mod.sh`.*
