using System;
using System.Runtime.CompilerServices;
using PluginSdk.Logging;

namespace Shared.Logging;

public class PluginLogger : LogFormatter, IPluginLogger
{
    private readonly Logger logger;

    public PluginLogger(string pluginName) : base("")
    {
        logger = Logger.Create(pluginName);
    }

    public bool IsTraceEnabled => true;
    public bool IsDebugEnabled => true;
    public bool IsInfoEnabled => true;
    public bool IsWarningEnabled => true;
    public bool IsErrorEnabled => true;
    public bool IsCriticalEnabled => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(Exception ex, string message, params object[] data) => logger.Debug(Format(ex, message, data));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(Exception ex, string message, params object[] data) => logger.Debug(Format(ex, message, data));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(Exception ex, string message, params object[] data) => logger.Info(Format(ex, message, data));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warning(Exception ex, string message, params object[] data) => logger.Warning(Format(ex, message, data));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(Exception ex, string message, params object[] data) => logger.Error(Format(null, message, data), ex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Critical(Exception ex, string message, params object[] data) => logger.Critical(Format(null, message, data), ex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string message, params object[] data) => Trace(null, message, data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string message, params object[] data) => Debug(null, message, data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(string message, params object[] data) => Info(null, message, data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warning(string message, params object[] data) => Warning(null, message, data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(string message, params object[] data) => Error(null, message, data);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Critical(string message, params object[] data) => Critical(null, message, data);
}
