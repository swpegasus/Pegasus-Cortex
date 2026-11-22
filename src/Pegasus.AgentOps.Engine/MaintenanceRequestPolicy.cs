using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Only fixed, reviewed operations confer read authority. Caller labels and
// history/snapshot transport choices cannot exempt an effect from maintenance.
internal static class MaintenanceRequestPolicy
{
    internal static bool IsMutation(OperationRequest request)
    {
        var action = request.Arg.GetValueOrDefault("op");
        return request.Op switch
        {
            "ctx" => request.Arg.ContainsKey("register"),
            "stat" or "ls" or "find" or "search" or "slice" or "diff" or "count" or "path" or "lines" or "disk" or "rd" or "read_lines" or "sha" or "diag" or "help" => false,
            "encoding" => action != "inspect",
            "registry" => !RegistryOperationContract.IsReadOnly(action),
            "certificate" => !CertificateOperationContract.IsReadOnly(action),
            "zip" or "7z" => action is not ("list" or "test"),
            "proc" => action is not ("ls" or "inspect" or "wait" or "tree" or "threads"),
            "net" => action != "list",
            "windows_service" => request.Arg.GetValueOrDefault("elevate") == "uac" || !WindowsServiceOperationContract.IsReadOnly(action),
            // An explicit UAC read still owns a privileged helper process and
            // its original one-use payload, so it participates in effect drain.
            "windows_task" => request.Arg.GetValueOrDefault("elevate") == "uac" || !WindowsTaskOperationContract.IsReadOnly(action),
            "installed_program" => !WindowsInstalledProgramOperationContract.IsReadOnly(action),
            "windows_device" => !WindowsDeviceOperationContract.IsReadOnly(action),
            "windows_network" => WindowsNetworkResolutionContract.IsRequest(request) || !WindowsNetworkOperationContract.IsReadOnly(action),
            "windows_account" => !WindowsAccountOperationContract.IsReadOnly(action),
            "connection_profile" => !ConnectionProfileOperationContract.IsReadOnly(action),
            "service" => action != "status",
            "job" => (action ?? "ls") is not ("ls" or "stat" or "log" or "wait"),
            "feedback" => action is not ("ls" or "get" or "export"),
            "tool" => action != "ls",
            "recipe" => action is not ("ls" or "get"),
            "ui" => action is not ("status" or "find" or "window" or "control"),
            "nsis" => action != "status",
            "app" => action is not ("status" or "inventory"),
            "terminal" => action is not ("list" or "status" or "read" or "recall" or "input_status" or "screen"),
            "queue" => action is not ("stat" or "status" or "ls"),
            "root" or "application_access" => action is not ("stat" or "status" or "ls" or "validate"),
            "admin" => action is not ("stat" or "status" or "interval" or "validate"),
            _ => true
        };
    }

    // Legacy receipt metadata has no request body. Unknown names are effects;
    // this list must stay narrower than the mutating operation vocabulary.
    internal static bool IsReadOnlyReceipt(string op) => op is
        "windows_task_list" or "windows_task_read" or "windows_task_instances" or "windows_task_prepare" or
        "windows_account_list" or "windows_account_read" or
        "ctx" or "ctx_ls" or "ctx_cur" or "stat" or "ls" or "find" or "search" or "slice" or "head" or "tail" or
        "diff" or "count" or "realpath" or "basename" or "dirname" or "sort" or "uniq" or "du" or "df" or
        "path" or "lines" or "disk" or "rd" or "read_lines" or "sha" or "diag" or "help" or "encoding_inspect" or
        "zip_list" or "zip_test" or "7z_list" or "7z_test" or "proc_ls" or "proc_inspect" or "proc_wait" or "proc_tree" or "proc_threads" or
        "windows_service_list" or "windows_service_read" or "installed_program_list" or "installed_program_read" or "windows_device_list" or "windows_device_read" or "windows_network_list" or "windows_network_read" or "net_list" or "service_status" or "job_ls" or "job_stat" or "job_log" or "job_wait" or
        "feedback_ls" or "feedback_get" or "feedback_export" or "tool_ls" or "recipe_ls" or "recipe_get" or
        "ui_status" or "ui_find" or "ui_window" or "ui_control" or "nsis_status" or "app_status" or "app_inventory" or
        "registry_inspect" or "registry_browse" or "registry_read" or
        "certificate_inspect" or "certificate_export_public" or "certificate_verify" or "certificate_store_list" or
        "connection_profile_list" or "connection_profile_read" or "connection_profile_inspect";
}
