using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PluginSdk.Commands;
using Shared.Config;
using Shared.Logging;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;
using ConfigStorage = PluginSdk.Config.ConfigStorage;
using ConcealmentConfig = Shared.Config.PluginConfig;

// Define assembly version when compiled by Magnetar
#if !DEV_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "Concealment";
    public static Plugin Instance { get; private set; }

    public long Tick { get; private set; }
    private static bool failed;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    public IPluginConfig Config => ConfigData;
    public ConcealmentConfig ConfigData { get; private set; }
    public ConcealmentManager Manager { get; private set; }

    private Harmony harmony;
    private string configPath;
    private static readonly string ConfigFileName = $"{Name}.cfg";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded
        Thread.Sleep(100);
#endif

        Instance = this;
        failed = false;

        Log.Info("Loading");

        configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        ConfigData = LoadConfig(configPath);
        ConfigData.PropertyChanged += ConfigOnPropertyChanged;

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, gameVersion, MyFileSystem.UserDataPath);

        harmony = new Harmony(Name);
        Manager = new ConcealmentManager(this, harmony);
        RegisterCommands();

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        try
        {
            Manager?.RevealAll();
            Manager?.Dispose();
            SaveConfig();

            if (ConfigData != null)
                ConfigData.PropertyChanged -= ConfigOnPropertyChanged;

            // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
        }

        Manager = null;
        ConfigData = null;
        Instance = null;
    }

    public void Update()
    {
        if (failed)
            return;
        
#if DEBUG
        CustomUpdate();
        Tick++;
#else        
        try
        {
            CustomUpdate();
            Tick++;
        }
        catch (Exception e)
        {
            Log.Critical(e, "Update failed");
            failed = true;
        }
#endif       
    }

    private void CustomUpdate()
    {
        Manager?.Update();
    }

    private ConcealmentConfig LoadConfig(string path)
    {
        try
        {
            var loaded = ConfigStorage.LoadXml<ConcealmentConfig>(path);
            ConfigStorage.SaveXml(loaded, path);
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load configuration file: {0}", path);

            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var corruptedPath = $"{path}.corrupted.{timestamp}.txt";
                Log.Info("Moving corrupted configuration file: {0} => {1}", path, corruptedPath);
                File.Move(path, corruptedPath);
            }
            catch (Exception moveEx)
            {
                Log.Warning(moveEx, "Failed to move corrupted configuration file");
            }

            var config = new ConcealmentConfig();
            ConfigStorage.SaveXml(config, path);
            return config;
        }
    }

    private void SaveConfig()
    {
        if (ConfigData == null || configPath == null)
            return;

        ConfigStorage.SaveXml(ConfigData, configPath);
    }

    private void ConfigOnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        try
        {
            SaveConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save configuration file: {0}", configPath);
        }
    }

    private void RegisterCommands()
    {
        try
        {
            ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ConcealmentCommands));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register chat commands");
        }
    }
}
