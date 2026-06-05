# Concealment

Server-only Space Engineers plugin for Magnetar.

## Prerequisites

- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Projects

- `ServerPlugin` - Magnetar plugin entry point and server runtime code.
- `Shared` - common plugin helpers and interfaces.

This repository builds only the Magnetar server plugin.

## Build

Install the Space Engineers Dedicated Server build references and the .NET SDK required by the project, then build:

```sh
dotnet build Concealment.sln -c Debug
```

The plugin output is:

```text
ServerPlugin/bin/Debug/net10.0/Concealment.dll
```

## Configuration

Magnetar stores configuration through the Plugin SDK config system.

## Deployment

Use the Magnetar local plugin folder or the included deploy scripts after a build:

```sh
ServerPlugin/Deploy.sh Concealment.dll ServerPlugin/bin/Debug/net10.0
```

On Windows:

```bat
ServerPlugin\Deploy.bat Concealment.dll ServerPlugin\bin\Debug\net10.0
```

`Concealment.xml` is the MagnetarHub metadata file for server-side publication.
