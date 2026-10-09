using System.Management.Automation;
using System.Text.Json;

namespace Iseberg.PowerShellHost;

public interface IIseScriptingBridge
{
    JsonElement Invoke(string operation, JsonElement arguments);
    string RegisterCallback(ScriptBlock action);
    void RemoveCallback(string handle);
    void ScheduleNotification(Action notification);
}

public interface IIseObjectProxyNotifications
{
    void Notify(string objectId, string propertyName);
}
