# Space Engineers Plugin Template

[Client only version of the template](https://github.com/viktor-ferenczi/se-client-plugin-template)

## Prerequisites

- [Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)
- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Pulsar](https://github.com/SpaceGT/Pulsar) — plugin loader for Space Engineers (game client)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Create your plugin project

1. Click on **Use this template** (top right corner on GitHub) and follow the wizard to create your repository
2. Clone your repository to have a local working copy
3. Run `setup.py`, enter the name of your plugin project in `CapitalizedWords` format
4. Let `setup.py` auto-detect your install locations or fill them in manually
5. Open the solution in Visual Studio or Rider
6. Make a test build, it should deploy the resulting files to their respective target folders (see them in the build log)
7. Test that the empty plugin can be enabled in Pulsar (client) and Magnetar (server)
8. Replace the contents of this file with the description of your plugin
9. Follow the TODO comments in the source code
10. Look into the source code of other plugins for examples on how to patch the game

You may find the source code of these plugins inspirational:
- [Performance Improvements](https://github.com/viktor-ferenczi/se-performance-improvements)
- [Multigrid Projector](https://github.com/viktor-ferenczi/se-multigrid-projector)
- [Toolbar Manager](https://github.com/viktor-ferenczi/se-toolbar-manager)

In case of questions please feel free to ask the SE plugin developer community on the
[Pulsar](https://discord.gg/z8ZczP2YZY) Discord server in their relevant text channels. They also have dedicated
channels for plugin ideas, should you look for a new one.

_Good luck!_

## Remarks

### Plugin configuration

You can have a nice configuration dialog with little effort in the game client.
Customize the `Config` class in the `ClientPlugin` project, just follow the examples.
It supports many different data types, including key binding. Once you have more
options than can fit on the screen the dialog will have a vertical scrollbar.

![Example config dialog](Docs/ConfigDialogExample.png "Example config dialog")

The server plugin configuration works differently, please see the `Config` folder
of the `Shared` project for that. The client side `Config` class is not integrated
with the server side configuration, currently.

### Shared project

- Put any code you can share between the plugin projects into the Shared project.
  Try to keep the redundancy at the minimum.

- The DLLs required by your Shared code need to be added as a dependency to all the projects,
  even if some of the code is not used by one of the projects.

- You can delete the projects you don't need. If you want only a single project,
  then move over what is in the Shared one, then you can delete Shared.

### How to prevent the potential crash after game updates

Please use the `EnsureCode` attribute on patch methods to safely skip loading the plugin
with an error logged should the code in any of the methods patched would change as part of
a game update. It is a good way to prevent blaming crashes on your plugin after game updates,
so your plugin can remain safely enabled (but effectively disabled) until you have a chance
to release an update for compatibility with the new game version. Please see the examples in
the `Shared/Patches` folder on how to use this attribute.

The hexadecimal hash code is logged in case of a mismatch, so you can read them from the logs
for any new method you patch, just leave the string initially empty in the `EnsureCode`
attribute, then replace with the value from the error log line after you run your plugin
with the patch for the first time.

On Proton (Linux) this check tends to cause issues, therefore there is a configuration flag
to turn it OFF. Setting the `SE_PLUGIN_DISABLE_METHOD_VERIFICATION` environment variable to
any value on the player's host also disables game code verification.

### Debugging

- Always use a debug build if you want to set breakpoints and see variable values.
- A debug build defines `DEBUG`, so you can add conditional code in `#if DEBUG` blocks.
- While debugging a specific target unload the other one. It prevents the IDE to be confused.
- If breakpoints do not "stick" or do not work, then make sure that:
  - Other projects are unloaded, only the debugged one and Shared are loaded.
  - Debugger is attached to the running process.
  - You are debugging the code which is running (no code changes made since the build).
- Transpiler patches will write a `harmony.log.txt` file to your `Desktop` while running `Debug`
  builds. Never release a debug build to your users, because that would litter their desktop
  as well.
- To debug transpiler changes to the IL code it is most practical to generate the files
  of the method's IL code before and after the change made, so you can just diff them.
  Please see the transpiler example under the `Shared/Patches` folder for the details.

### Accessing internal, protected and private members in game code

Enable the Krafs publicizer to significantly reduce the amount of reflections you need to write.

This can be done by systematically uncommenting the code sections marked with "Uncomment to enable publicizer support".
Make sure not to miss any of those. List the game assemblies you need to publicize in `GameAssembliesToPublicize.cs`. 
In case of problems read about the [Krafs Publicizer](https://github.com/krafs/Publicizer) or reach out on the [Pulsar](https://discord.gg/z8ZczP2YZY) Discord server.

### AI assisted plugin development

Please consider using [se-dev-skills](https://github.com/viktor-ferenczi/se-dev-skills/) for better outcomes.

### Troubleshooting

- If the IDE looks confused, then restarting it and the debugged game usually works.
- If the restart did not work, then try to delete caches used by your IDE and restart.
- If your build cannot deploy (just runs in a loop), then something locks the DLL file.
- Look for running game processes (maybe stuck running in the background) and kill them.

### Release

- Always make your final release from a RELEASE build. (More optimized, removes debug code.)
- Always test your RELEASE build before publishing. Sometimes it behaves differently.
- In case of client plugins the Pulsar compiles your code, watch out for differences.

### Publishing your plugin

- Register **client** plugins into [PluginHub](https://github.com/StarCpt/PluginHub/),
  so they become available in Pulsar.
- Register **server** plugins into [MagnetarHub](https://github.com/viktor-ferenczi/MagnetarHub),
  so they become available in Magnetar.

### Communication

- In your documentation always include how players or server admins should report bugs.
- Try to be reachable and respond on a timely manner over your communication channels.
- Be open for constructive critics.

### Abandoning your project

- Always consider finding a new maintainer, ask around at least once.
- If you ever abandon the project, then make it clear on its GitHub page.
- Abandoned projects should be made hidden on PluginHub and MagnetarHub.
- Keep the code available on GitHub, so it can be forked and continued by others.
