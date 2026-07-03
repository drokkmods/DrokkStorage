# Drokk Storage

Allows multiple players to view storage containers and workbenches simultaneously.

**Author:** drokk
**Version:** 1.0.2
A mod for *7 Days to Die*.

## Install (no compiling needed)

1. Download `DrokkStorage-v1.0.2.zip` from the [latest release](https://github.com/drokkmods/DrokkStorage/releases/latest).
2. Extract it into your `7 Days To Die/Mods/` folder so you end up with `Mods/DrokkStorage/`.
3. Launch the game. On multiplayer, the server and all clients need the mod.

## Build from source

You need the .NET SDK and a copy of the game (for the reference assemblies).

```bash
# Point the build at your game install (the folder containing 7DaysToDie_Data/)
dotnet build DrokkStorage.csproj -c Release -p:GamePath="/path/to/7 Days To Die"
```

The compiled `DrokkStorage.dll` plus `ModInfo.xml` and `Config/` make up the deployable mod.

## Configuration

This mod reads its settings from `Config/settings.xml` inside the mod folder
(`Mods/DrokkStorage/Config/settings.xml`). Open it in any text editor, change the
`value="..."` numbers, and restart the game to apply. Each setting is documented
with a comment in the file. If the file is missing or a value is invalid, the mod
falls back to its built-in defaults.

---
*Exported from a private monorepo with `export_mod.sh`.*
