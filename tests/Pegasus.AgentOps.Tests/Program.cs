using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Microsoft.Data.Sqlite;

if(Environment.GetEnvironmentVariable("DOTNET_ROOT") is not{Length:>0}&&Environment.GetEnvironmentVariable("PAO_TEST_DOTNET") is{Length:>0} testDotnet&&File.Exists(testDotnet))
    Environment.SetEnvironmentVariable("DOTNET_ROOT",Path.GetDirectoryName(Path.GetFullPath(testDotnet)));
if(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64") is not{Length:>0}&&Environment.GetEnvironmentVariable("PAO_TEST_DOTNET") is{Length:>0} testDotnetX64&&File.Exists(testDotnetX64))
    Environment.SetEnvironmentVariable("DOTNET_ROOT_X64",Path.GetDirectoryName(Path.GetFullPath(testDotnetX64)));

if(args.Length>0&&args[0]=="--owner-admin-argv-probe"){Console.WriteLine(JsonSerializer.Serialize(args[1..]));return 0;}

if(args is ["--app-candidate",var applicationPlan]) return await ApplicationCandidateAcceptance.RunAsync(applicationPlan);

if(args is ["--capture-server",var capturePipe])
{
    try{await using var pipe=new NamedPipeClientStream(".",capturePipe,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation);await pipe.ConnectAsync(2000);var peer=LocalIdentity.CaptureExpectedServer(pipe,new LocalPeer("S-1-5-18",0));Console.WriteLine($"SERVER sid={peer.Sid} session={peer.Session}");return 0;}
    catch(Exception ex){Console.WriteLine($"SERVER_ERROR {ex}");return 1;}
}

var consoleServiceIdentity=LocalIdentity.Current();
Environment.SetEnvironmentVariable("PAO_TEST_SERVICE_SID",consoleServiceIdentity.Sid);
Environment.SetEnvironmentVariable("PAO_TEST_SERVICE_SESSION",consoleServiceIdentity.Session.ToString());
Environment.SetEnvironmentVariable("PAO_TEST_IDENTITY_FIXTURE","1");

if (args is ["--service-exit-policy-only"])
{
    await ServiceExitAdmissionTests.RunAsync();
    return 0;
}
if (args is ["--service-startup-path-only"])
{
    await ServiceStartupPathTests.RunAsync();
    await PrivilegedCodePathTests.RunAsync();
    return 0;
}
if (args is ["--live-service-startup-diagnostics"])
{
    await ServiceStartupPathTests.ReadLiveDiagnosticsAsync();
    return 0;
}
if (args is ["--owner-admin-state-boundary-only"])
{
    await OwnerAdminStateBoundaryTests.RunAsync();
    return 0;
}
if (args is ["--shared-mcp-hosting-only"])
{
    await CortexMcpHostingTests.RunAsync();
    await LocalCertificatePasswordLifetimeTests.RunMcpSetupAsync();
    return 0;
}
if (args is ["--shared-mcp-relay-only"])
{
    await CortexSharedMcpRelayTests.RunAsync();
    return 0;
}
if (args is ["--shared-mcp-configured-loopback-only", var mcpLoopbackConfiguration])
{
    await CortexMcpConfiguredLoopbackTests.RunAsync(mcpLoopbackConfiguration);
    return 0;
}
if (args is ["--processor-policy-only"])
{
    await ProcessorPolicyTests.RunAsync();
    return 0;
}
if (args is ["--copy-transfer-only"])
{
    await CopyCancellationTests.RunAsync();
    await CopyJobWiringTests.RunAsync();
    await CopyPoliciesAsync();
    await CopyRelationshipsAsync();
    await CopyReparseAsync();
    Console.WriteLine("PASS existing copy policies, relationship refusals and owned junction safety");
    return 0;
}
if (args is ["--zip-transfer-only"])
{
    await ZipJobWiringTests.RunAsync();
    return 0;
}

if (args is ["--job-history-only"])
{
    await JobHistoryRetentionTests.RunAsync();
    await JobHistoryContractTests.RunAsync();
    return 0;
}

if (args is ["--job-submission-only"])
{
    await JobSubmissionProjectionTests.RunAsync();
    return 0;
}

if (args is ["--execution-history-retention-only"])
{
    await ExecutionHistoryRetentionTests.RunAsync();
    return 0;
}

if (args is ["--registry-wiring-only"])
{
    await RegistryWiringTests.RunAsync();
    await RegistryFrontendTests.RunAsync();
    return 0;
}

if (args is ["--general-windows-services-only"])
{
    await GeneralWindowsServiceTests.RunAsync();
    return 0;
}
if (args is ["--general-windows-services-read-only"])
{
    await GeneralWindowsServiceTests.RunNativeReadOnlyAsync();
    return 0;
}

if (args is ["--certificate-contract-only"])
{
    await CertificateContractTests.RunAsync();
    return 0;
}
if (args is ["--machine-server-elevation-only"])
{
    await MachineServerCertificateElevationTests.RunAsync();
    return 0;
}
if (args is ["--certificate-elevation-only"])
{
    await CertificateElevationContractTests.RunAsync();
    await CertificateStoreElevationTests.RunAsync();
    await CertificateAdministrationTests.RunAsync();
    return 0;
}
if (args is ["--scheduled-task-elevation-only"])
{
    await ScheduledTaskElevationTests.RunAsync();
    await ScheduledTaskElevationWiringTests.RunAsync();
    return 0;
}
if (args is ["--windows-service-elevation-only"])
{
    await WindowsServiceElevationTests.RunAsync();
    await WindowsServiceElevationWiringTests.RunAsync();
    return 0;
}
if (args is ["--certificate-wiring-only"])
{
    await CertificateWiringTests.RunAsync();
    return 0;
}
if (args is ["--certificate-store-exchange-only"])
{
    await CertificateStoreExchangeTests.RunAsync();
    return 0;
}
if (args is ["--certificate-secret-only"])
    return await CertificateSecretTests.RunAsync();
if (args is ["--certificate-password-store-only"])
    return await OwnerCertificateSecretStoreTests.RunAsync();
if (args is ["--certificate-password-lifetime-only"])
{
    await LocalCertificatePasswordLifetimeTests.RunAsync();
    return 0;
}
if (args is ["--installed-programs-only"])
{
    await GeneralInstalledProgramTests.RunAsync();
    await InstalledProgramWiringTests.RunAsync();
    return 0;
}
if (args is ["--windows-devices-only"])
{
    await WindowsDeviceTests.RunAsync();
    await WindowsInventoryWiringTests.RunDevicesAsync();
    return 0;
}
if (args is ["--windows-tasks-only"])
{
    await WindowsTaskTests.RunAsync();
    await WindowsTaskToolResolverTests.RunAsync();
    await WindowsTaskWiringTests.RunAsync();
    return 0;
}
if (args is ["--windows-tasks-read-only"])
{
    if (!OperatingSystem.IsWindows()) return 77;
    await WindowsTaskTests.RunNativeReadOnlyAsync();
    return 0;
}
if (args is ["--windows-devices-read-only"])
    return await WindowsDeviceTests.RunNativeReadOnlyAsync();
if (args is ["--windows-network-only"])
{
    await WindowsNetworkTests.RunAsync();
    await WindowsInventoryWiringTests.RunNetworkAsync();
    return 0;
}
if (args is ["--windows-network-resolution-only"])
{
    await WindowsNetworkResolutionTests.RunAsync();
    await WindowsInventoryWiringTests.RunResolutionMetadataAsync();
    await WindowsNetworkResolutionWiringTests.RunAsync();
    return 0;
}
if (args is ["--windows-accounts-only"])
{
    await WindowsAccountTests.RunAsync();
    await WindowsAccountNativeTests.RunAsync();
    await WindowsInventoryWiringTests.RunAccountsAsync();
    return 0;
}
if (args is ["--windows-network-read-only"])
{
    if (!OperatingSystem.IsWindows()) return 77;
    await WindowsNetworkTests.RunNativeReadOnlyAsync();
    return 0;
}
if (args is ["--connection-profiles-only"])
{
    var profiles = await SmbConnectionProfileTests.RunAsync();
    if (profiles != 0) return profiles;
    var mappings = await WindowsSmbConnectionTests.RunAsync();
    if (mappings != 0) return mappings;
    await ConnectionProfileWiringTests.RunAsync();
    return 0;
}
if (args is ["--certificate-persistence-create-only", var certificateRoot])
    return await CertificatePersistenceTests.CreateAsync(certificateRoot);
if (args is ["--certificate-persistence-reopen-only", var persistedRoot, var caId, var leafId])
    return await CertificatePersistenceTests.ReopenAsync(persistedRoot, caId, leafId);

if (args is ["--operation-discovery-only"])
{
    await OperationDiscoveryTests.RunAsync();
    return 0;
}

if (args is ["--process-threads-only"])
{
    await ProcessThreadContractTests.RunAsync();
    return 0;
}

if (args is ["--process-threads-windows-only"])
    return await ProcessThreadContractTests.RunWindowsAsync();

var tests = new (string Name, Func<Task> Run)[]
{
    ("copy_exact_and_policies", CopyPoliciesAsync),
    ("copy_cancellation_and_partial_progress", CopyCancellationTests.RunAsync),
    ("copy_background_job_cancellation_and_ownership", CopyJobWiringTests.RunAsync),
    ("zip_background_job_cancellation_and_ownership", ZipJobWiringTests.RunAsync),
    ("copy_relationships_no_mutation", CopyRelationshipsAsync),
    ("copy_reparse_root_no_external_delete", CopyReparseAsync),
    ("network_share_root_copy_admission", NetworkShareRootCopyAdmissionAsync),
    ("workspace_junction_ancestor_no_mutation", WorkspaceJunctionAsync),
    ("absolute_junction_pipe_no_mutation", AbsoluteJunctionPipeAsync),
    ("forged_path_roots_rejected_over_binary_pipe", ForgedPathRootsAsync),
    ("forged_system_context_rejected_over_binary_pipe", ForgedSystemContextAsync),
    ("binary_pipe_version", BinaryProtocolAsync),
    ("phase2_text_encoding", Phase2Async),
    ("text_write_preimage_and_guard_evidence", TextPreimageContractTests.RunAsync),
    ("pwsh_argumentlist_real", PowerShellAsync),
    ("pwsh_installed_bootstrap_policy", PowerShellBootstrapTests.RunAsync),
    ("service_trace_private_ownership", ServiceTraceSecurityTests.RunAsync),
    ("service_process_creation_owner", ServiceProcessOwnershipTests.RunAsync),
    ("service_startup_code_identity", ServiceStartupPathTests.RunAsync),
    ("privileged_code_ancestor_access", PrivilegedCodePathTests.RunAsync),
    ("processor_policy", ProcessorPolicyTests.RunAsync),
    ("shared_mcp_hosting", CortexMcpHostingTests.RunAsync),
    ("shared_mcp_relay", CortexSharedMcpRelayTests.RunAsync),
    ("owner_admin_state_boundary", OwnerAdminStateBoundaryTests.RunAsync),
    ("sqlite_receipt_idempotency", ReceiptAsync),
    ("sqlite_history_rollover_and_non_durable_health", JobHistoryMemoryTests.RunAsync),
    ("sqlite_history_retention_preserves_outcomes_and_replay", JobHistoryRetentionTests.RunAsync),
    ("history_cli_mcp_scope_and_projection", JobHistoryContractTests.RunAsync),
    ("registry_owner_engine_privacy_replay_and_recovery", RegistryWiringTests.RunAsync),
    ("registry_cli_mcp_strict_contract_and_projection", RegistryFrontendTests.RunAsync),
    ("general_windows_services", GeneralWindowsServiceTests.RunAsync),
    ("certificate_closed_cli_mcp_contract", CertificateContractTests.RunAsync),
    ("certificate_owner_privacy_replay_and_recovery", CertificateWiringTests.RunAsync),
    ("certificate_store_and_file_exchange_privacy", CertificateStoreExchangeTests.RunAsync),
    ("certificate_elevation_closed_payload_contract", CertificateElevationContractTests.RunAsync),
    ("certificate_elevation_one_shot_lifetime", CertificateStoreElevationTests.RunAsync),
    ("certificate_elevation_owner_engine_durability", CertificateAdministrationTests.RunAsync),
    ("machine_server_elevation_and_owner_durability", MachineServerCertificateElevationTests.RunAsync),
    ("operation_discovery_selected_schema_and_identity_routes", OperationDiscoveryTests.RunAsync),
    ("process_threads_identity_bounds_and_owner_route", ProcessThreadContractTests.RunAsync),
    ("sqlite_bounded_recovery_window_and_disk_wait", BoundedRecoveryTests.RunAsync),
    ("sqlite_cancellation_rollover_and_scalar_health", JobHistoryMemoryTests.CancellationAndScalarHealthAsync),
    ("sqlite_concurrent_same_rid", ConcurrentRidAsync),
    ("sqlite_restart_replays_queued", RestartQueuedAsync),
    ("canceled_queued_clean_never_runs", CancelQueuedAsync),
    ("queued_clean_alias_rebind_frozen", AliasRebindAsync),
    ("cross_workspace_copy_alias_rebind_frozen", CrossWorkspaceAliasRebindAsync),
    ("job_stat_immediate_wait_terminal", JobStatWaitAsync),
    ("job_submission_projection", JobSubmissionProjectionTests.RunAsync),
    ("execution_history_retention", ExecutionHistoryRetentionTests.RunAsync),
    ("local_job_log_route_parity", LocalJobLogAsync),
    ("service_job_cancel_retry_log", ServiceJobControlAsync),
    ("tray_pause_resume_is_durable", TrayPauseResumeAsync),
    ("typed_dotnet_git_argv", TypedAdapterArgumentsAsync),
    ("dotnet_registration_sets_user_package_paths", DotnetRegistrationEnvironmentAsync),
    ("dotnet_registered_environment_builds_existing_assets_offline", () => DotnetSdkEnvironmentTests.RunAsync(Dotnet())),
    ("external_output_bounded_and_marked", ExternalOutputBoundedAsync),
    ("phase2_cli_body_forms", Phase2CliBodyFormsAsync),
    ("line_edit_authenticated_broker", LineEditBrokerAsync),
    ("text_context_encoding_help_contract", TextContextEncodingHelpAsync),
    ("mcp_agent_output_projection", McpOutputProjectionTests.RunAsync),
    ("deferred_submission_client_contract", DeferredSubmissionClientTests.RunAsync),
    ("deferred_submission_mcp_bounded_calls", DeferredSubmissionClientTests.McpBoundedCallsAsync),
    ("deferred_submission_engine_lifecycle", DeferredSubmissionEngineTests.RunAsync),
    ("deferred_submission_engine_capacity", DeferredSubmissionEngineTests.CapacityAsync),
    ("deferred_submission_engine_recovery", DeferredSubmissionEngineTests.RecoveryAsync),
    ("bounded_mutation_scheduler", BoundedMutationSchedulerTests.RunAsync),
    ("named_pipe_independent_calls", NamedPipeDispatcherConcurrencyTests.RunAsync),
    ("maintenance_engine_admission_replay", MaintenanceEngineTests.AdmissionAndReplayAsync),
    ("maintenance_public_reader_path", MaintenanceEngineTests.PublicReaderPathAsync),
    ("maintenance_public_reader_missing_closed", MaintenanceEngineTests.PublicReaderMissingClosedAsync),
    ("maintenance_reader_failure_labels", MaintenanceEngineTests.ReaderFailureLabelsAsync),
    ("maintenance_reader_records_stay_fenced", MaintenanceEngineTests.ReaderRecordsStayFencedAsync),
    ("maintenance_engine_held_queue_shutdown", MaintenanceEngineTests.HeldQueuedAndShutdownAsync),
    ("maintenance_engine_recovery_probe_ack", MaintenanceEngineTests.RecoveryProbeAndAcknowledgementAsync),
    ("maintenance_engine_frozen_active_probe", MaintenanceEngineTests.FrozenDispatchAndActiveProbeAsync),
    ("maintenance_engine_application_replay", MaintenanceEngineTests.ApplicationReplayFenceAsync),
    ("maintenance_interval_missing_public_state", MaintenanceIntervalTests.MissingPublicStateAsync),
    ("maintenance_interval_many_attempts", MaintenanceIntervalTests.ManyAttemptsHandbackAsync),
    ("maintenance_interval_startup_effects", MaintenanceIntervalTests.StartupEffectsAsync),
    ("maintenance_interval_lifecycle", MaintenanceIntervalTests.LifecycleAsync),
    ("maintenance_interval_broker_race_ack", MaintenanceIntervalTests.BrokerRaceAndAcknowledgementAsync),
    ("maintenance_interval_restart_loss", MaintenanceIntervalTests.RestartAndLossAsync),
    ("maintenance_init_fresh", MaintenanceIntervalTests.InitializationFreshAsync),
    ("maintenance_init_existing_fence", MaintenanceIntervalTests.InitializationExistingFenceAsync),
    ("maintenance_init_before_query_failure", MaintenanceIntervalTests.InitializationBeforeQueryFailureAsync),
    ("maintenance_init_after_query_failure", MaintenanceIntervalTests.InitializationAfterQueryFailureAsync),
    ("maintenance_init_privacy_auth", MaintenanceIntervalTests.InitializationPrivacyAndAuthenticationAsync),
    ("owner_admin_invocation_deadline_at_claim", OwnerAdminExecutionTests.InvocationDeadlineAtClaimAsync),
    ("owner_admin_security", OwnerAdminExecutionTests.SecurityAsync),
    ("owner_admin_interval_cli_mcp", OwnerAdminExecutionTests.IntervalCliMcpAsync),
    ("owner_admin_reusable_replay", OwnerAdminExecutionTests.ReusableAndReplayAsync),
    ("owner_admin_observation_recovery", OwnerAdminExecutionTests.ObservationAndRecoveryAsync),
    ("owner_admin_cli_mcp", OwnerAdminExecutionTests.CliMcpAsync),
    ("owner_admin_concurrent_failed_launch", OwnerAdminExecutionTests.ConcurrentAndFailedLaunchAsync),
    ("owner_admin_authenticated_pipe", OwnerAdminExecutionTests.AuthenticatedPipeAsync),
    ("owner_admin_stdio", OwnerAdminExecutionTests.StdioAsync),
    ("owner_admin_fencing_storage_expiry", OwnerAdminExecutionTests.FencingStorageAndExpiryAsync),
    ("owner_admin_nested_mutation", OwnerAdminExecutionTests.NestedMutationAsync),
    ("owner_admin_real_medium_argv", OwnerAdminExecutionTests.ArgvAsync),
    ("registered_run_timeout_admission_evidence", RegisteredRunAdmissionTests.RunAsync),
    ("mcp_candidate_help_transcript", CandidateHelpTranscriptAsync),
    ("user_application_active_capacity_evidence", ApplicationCapacityTests.ActiveAsync),
    ("user_application_retained_capacity_evidence", ApplicationCapacityTests.RetainedAsync),
    ("user_application_capacity_retained_before_observation", ApplicationCapacityTests.RetainedBeforeObservationAsync),
    ("user_application_capacity_projection", ApplicationCapacityTests.ProjectionAsync),
    ("user_application_inventory_release", ApplicationCapacityTests.InventoryReleaseAsync),
    ("user_application_inventory_unknown_retirement", ApplicationCapacityTests.InventoryUnknownAndRetirementAsync),
    ("user_application_release_successor", ApplicationCapacityTests.ReleasePreservesSuccessorAsync),
    ("user_application_maintenance_projection", ApplicationCapacityTests.MaintenanceProjectionAsync),
    ("user_application_inventory_active", ApplicationCapacityTests.InventoryAtActiveCapacityAsync),
    ("user_application_maintenance_engine_replay", UserApplicationLifecycleTests.MaintenanceEngineReplayAsync),
    ("user_application_workspace_alias_diagnostics", ApplicationWorkspaceAliasTests.RunAsync),
    ("user_application_owned_trees", UserApplicationLifecycleTests.OwnedTreesAsync),
    ("user_application_root_exit_restart_retry", UserApplicationLifecycleTests.RootExitRestartAndRetryAsync),
    ("user_application_owner_disposal", UserApplicationLifecycleTests.OwnerDisposalAsync),
    ("user_application_user_boundary_and_surface", UserApplicationLifecycleTests.UserBoundaryAndSurfaceAsync),
    ("user_application_engine_replay_and_recovery", UserApplicationLifecycleTests.EngineReplayAndRecoveryAsync),
    ("user_application_malformed_frames_unconfirmed_stop", UserApplicationLifecycleTests.MalformedFramesAndUnconfirmedStopAsync),
    ("ordinary_run_cleans_detached_children", UserApplicationLifecycleTests.OrdinaryRunCleanupAsync),
    ("compact_parser_and_exit_taxonomy", CompactParserAndExitTaxonomyAsync),
    ("unix_compatibility_parser", UnixCompatibilityParserAsync),
    ("unix_native_find_search_tail_diff", UnixNativeOperationsAsync),
    ("unix_cli_mcp_service_equivalence", UnixServiceEquivalenceAsync),
    ("service_ephemeral_body_lifecycle", ServiceEphemeralBodyAsync),
    ("workspace_root_remove_rejected", WorkspaceRootRemoveAsync),
    ("no_ws_caller_cwd_root_remove_rejected", NoWorkspaceCallerCwdAsync),
    ("transport_disconnect_preserves_admitted_job", TransportDisconnectAsync),
    ("broker_route_loss_waiter_and_foreign_quarantine", BrokerRouteLossAndForeignAsync),
    ("root_code_versioned_store_management", RootCodeStoreAsync),
    ("root_code_visible_policy_enforcement", RootCodePolicyAsync),
    ("owner_root_tool_recipe_controls", OwnerControlAsync),
    ("registered_7z_public_adapter", RegisteredSevenZipAsync),
    ("service_waits_without_authenticated_tray", ServiceWaitsForTrayAsync),
    ("authenticated_tray_executes_owner_identity", AuthenticatedTrayAsync),
    ("wrong_tray_sid_session_rejected", WrongTrayIdentityAsync),
    ("owner_session_change_resumes_once", OwnerSessionChangeAsync),
    ("installed_config_strict_process", InstalledConfigProcessAsync),
    ("pipe_submission_failure_is_recovery", PipeSubmissionAmbiguityAsync),
    ("routing_user_host_service_boundary", RoutingBoundaryAsync),
    ("nested_mutation_fails_before_single_owner_queue", NestedMutationGuardAsync),
    ("routing_user_host_absence_never_falls_back_to_service", RoutingFallbackAsync),
    ("user_host_exit_admission_and_startup", UserHostLifecycleTests.AdmissionAndStartupAsync),
    ("user_host_exit_engine_without_service", UserHostLifecycleTests.EngineExitAdmissionAsync),
    ("user_host_exit_preserves_unknown_work", UserHostLifecycleTests.UnknownWorkRefusesExitAsync),
    ("direct_user_host_pipe_without_service", DirectUserHostWithoutServiceAsync),
    ("root_system_dispatch_does_not_require_tray", RootWithoutTrayAsync),
    ("broker_database_sequence_is_execution_gate", BrokerSequenceGateAsync),
    ("stale_tray_failure_preserves_reconnect", TrayReconnectRaceAsync),
    ("system_diag_does_not_replay_state", SystemDiagNoReplayAsync),
    ("release_identity_overrides_absent", ReleaseIdentityOverridesAsync),
    ("no_tray_alias_rebind_frozen", ServiceAliasFreezeAsync),
    ("repeated_tray_registration_one_execution", RepeatedTrayRegistrationAsync),
    ("tray_commit_crash_recovery_no_replay", TrayCommitCrashAsync),
    ("squatted_system_pipe_cannot_capture_root", SquattedSystemPipeAsync),
    ("pipe_acl_server_client_rights", PipeAclAsync),
    ("pipe_client_application_identity", PipeClientApplicationAsync),
    ("root_code_system_fixed_action", RootSystemDispatchAsync),
    ("application_credential_lifecycle_and_redaction", ApplicationAuthorizationTests.CredentialLifecycleAsync),
    ("application_session_full_typed_surface", ApplicationAuthorizationTests.TypedSystemSurfaceAsync),
    ("application_session_missing_system_7z_fails_closed", ApplicationAuthorizationTests.MissingSystemSevenZipAsync),
    ("user_system_single_fifo_order", UserSystemFifoAsync),
    ("service_two_client_fifo", TwoClientFifoAsync),
    ("worker_survives_start_failure", WorkerSurvivalAsync),
    ("proc_stop_exact_identity_fifo", ProcStopAsync),
    ("service_lifecycle_root_only_exact_identity", ServiceLifecycleTests.RunAsync),
    ("cli_without_mcp_process", CliFallbackAsync),
    ("mcp_stdio_process_transcript", McpTranscriptAsync),
    ("mcp_git_cwd_detail_contract", McpGitCwdDetailAsync),
    ("mcp_dotnet_solution_contract", McpDotnetSolutionAsync),
    ("mcp_proc_ls_pattern_max_contract", McpProcessListFilterAsync),
    ("ui_mcp_cli_help_contract", UiParserContractAsync),
    ("ui_service_ephemeral_and_system_boundary", UiServiceBoundaryAsync),
    ("ui_cli_mcp_broker_status_equivalence", UiBrokerEquivalenceAsync),
    ("registered_nsis_validate_build_contract", NsisAdapterAsync),
    ("feedback_mcp_service_durable_export", FeedbackInboxAsync),
    ("mcp_pipe_down_stays_alive", McpPipeDownAsync),
    ("mcp_request_cancellation", McpCancellationAsync),
    ("service_pipe_process", ServicePipeAsync)
    ,("tray_queue_monitor_process", TrayProcessAsync)
    ,("tray_winforms_smoke_process", TrayWinFormsSmokeAsync)
    ,("ui_host_resolution_skips_incomplete_nearer_payload", UiHostResolutionAsync)
    ,("tray_ui_provider_inherited_job_process", TrayUiProviderSmokeAsync)
    ,("tray_service_unavailable_retries", TrayUnavailableRetryAsync)
};

var selectedTests=args.Length==0?tests:tests.Where(test=>args.Contains(test.Name,StringComparer.Ordinal)).ToArray();
var failed = 0;
foreach (var test in selectedTests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name} {ex}"); }
}
Console.WriteLine($"RESULT total={selectedTests.Length} passed={selectedTests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static Task CopyPoliciesAsync()
{
    using var temp = new TempRoot();
    var src = temp.Dir("src"); File.WriteAllText(Path.Combine(src, "a.txt"), "new");
    var dst = temp.Dir("dst"); File.WriteAllText(Path.Combine(dst, "a.txt"), "old"); File.WriteAllText(Path.Combine(dst, "unrelated.txt"), "keep");
    Throws("collision", () => CopyEngine.Copy(src, dst, "fail"));
    var replace = CopyEngine.Copy(src, dst, "replace");
    Equal("new", File.ReadAllText(Path.Combine(dst, "a.txt"))); Equal("keep", File.ReadAllText(Path.Combine(dst, "unrelated.txt"))); Equal(1L, replace.Copied);
    File.WriteAllText(Path.Combine(dst, "a.txt"), "existing");
    var skip = CopyEngine.Copy(src, dst, "skip"); Equal("existing", File.ReadAllText(Path.Combine(dst, "a.txt"))); Equal(0L, skip.Copied);
    var clean = CopyEngine.Copy(src, dst, "clean"); Equal("new", File.ReadAllText(Path.Combine(dst, "a.txt"))); Check(!File.Exists(Path.Combine(dst, "unrelated.txt")), "clean retained unrelated entry"); Check(clean.Deleted >= 2, "clean count");
    return Task.CompletedTask;
}

static Task CopyRelationshipsAsync()
{
    using var temp = new TempRoot();
    var src = temp.Dir("src"); var marker = Path.Combine(src, "marker.txt"); File.WriteAllText(marker, "safe");
    foreach (var pol in new[] { "fail", "replace", "skip", "clean" }) Throws("precondition", () => CopyEngine.Copy(src, src, pol));
    Throws("precondition", () => CopyEngine.Copy(src, Path.Combine(src, "child"), "replace"));
    var parent = temp.Dir("parent"); var nested = Directory.CreateDirectory(Path.Combine(parent, "nested")).FullName; File.WriteAllText(Path.Combine(nested, "source.txt"), "safe");
    Throws("precondition", () => CopyEngine.Copy(nested, parent, "clean"));
    Equal("safe", File.ReadAllText(marker)); Equal("safe", File.ReadAllText(Path.Combine(nested, "source.txt"))); Check(!Directory.Exists(Path.Combine(src, "child")), "relationship rejection mutated destination");
    return Task.CompletedTask;
}

static async Task CopyReparseAsync()
{
    using var temp = new TempRoot();
    var src = temp.Dir("src"); File.WriteAllText(Path.Combine(src, "new.txt"), "new");
    var external = temp.Dir("external"); var marker = Path.Combine(external, "do-not-delete.txt"); File.WriteAllText(marker, "safe");
    var link = Path.Combine(temp.Path, "dst-link");
    var junction = await RunProcessAsync(Pwsh(), ["-NoProfile", "-NonInteractive", "-File", JunctionScript(), link, external]);
    Equal(0, junction.ExitCode);
    Throws("reparse_boundary", () => CopyEngine.Copy(src, link, "clean"));
    Equal("safe", File.ReadAllText(marker));
    Directory.Delete(link);
}

static async Task NetworkShareRootCopyAdmissionAsync()
{
    using var temp = new TempRoot();
    var suffix = Guid.NewGuid().ToString("N");
    var sourceRoot = $@"\\localhost\pao-missing-source-{suffix}\";
    var destinationRoot = $@"\\localhost\pao-missing-destination-{suffix}\";
    var owner = LocalIdentity.Current().Sid;
    var peer = new LocalPeer(owner, 3);
    var dispatched = 0;

    await using (var broker = new AuthenticatedServiceBroker(
        temp.Dir("network-service-state"),
        owner,
        new RootCodeVerifier(Path.Combine(temp.Path, "network-root.verifier")),
        false,
        (_, _, request, _) =>
        {
            Interlocked.Increment(ref dispatched);
            Equal(sourceRoot, request.Arg["src"]);
            Equal(destinationRoot, request.Arg["dst"]);
            Equal("replace", request.Arg["pol"]);
            return Task.FromResult(new OperationReceipt(true, "inner", request.Rid!, "done", request.Op));
        }))
    {
        Check((await broker.RegisterTrayAsync(new TrayRegistration(owner, 3, "network-root-tray"), peer)).Ok, "network test tray registration failed");
        var receipt = await broker.HandleUserAsync(
            new OperationRequest("cp", new Dictionary<string, string>
            {
                ["src"] = sourceRoot,
                ["dst"] = destinationRoot,
                ["pol"] = "replace"
            }, "network-root-service"),
            peer,
            CancellationToken.None);
        Check(receipt.Ok, receipt.Err ?? "network root was rejected by service admission");
    }
    Equal(1, dispatched);

    await using var engine = new AgentOpsEngine(temp.Dir("network-tray-state"));
    var userContext = await engine.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest(
        "cp",
        new Dictionary<string, string>
        {
            ["src"] = sourceRoot,
            ["dst"] = destinationRoot,
            ["pol"] = "replace"
        },
        "network-root-tray"));
    Equal("not_found", userContext.Err);
}

static async Task WorkspaceJunctionAsync()
{
    using var temp=new TempRoot();var ws=temp.Dir("ws");var src=Directory.CreateDirectory(Path.Combine(ws,"src")).FullName;File.WriteAllText(Path.Combine(src,"a.txt"),"a");var outside=temp.Dir("outside");var marker=Path.Combine(outside,"marker.txt");File.WriteAllText(marker,"safe");var link=Path.Combine(ws,"link");var junction=await RunProcessAsync(Pwsh(),["-NoProfile","-NonInteractive","-File",JunctionScript(),link,outside]);Equal(0,junction.ExitCode);
    try{await using var engine=new AgentOpsEngine(temp.Dir("state"));Check((await engine.ExecuteAsync(new OperationRequest("ctx",new Dictionary<string,string>{{"register",$"demo:{ws}"}}))).Ok,"register");var receipt=await engine.ExecuteAsync(new OperationRequest("cp",new Dictionary<string,string>{{"src","src"},{"dst","link\\child"},{"pol","clean"}},Ws:"demo"));Equal("reparse_boundary",receipt.Err);Equal("safe",File.ReadAllText(marker));Check(!Directory.Exists(Path.Combine(outside,"child")),"outside mutated");}finally{if(Directory.Exists(link))Directory.Delete(link);}
}

static async Task AbsoluteJunctionPipeAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var src=temp.Dir("src");File.WriteAllText(Path.Combine(src,"new.txt"),"new");var outside=temp.Dir("outside");var marker=Path.Combine(outside,"marker.txt");File.WriteAllText(marker,"safe");var link=Path.Combine(temp.Path,"link");var junction=await RunProcessAsync(Pwsh(),["-NoProfile","-NonInteractive","-File",JunctionScript(),link,outside]);Equal(0,junction.ExitCode);
    try{using var service=await StartServiceAsync(state);var rejected=await RunPaoAsync(["cp",src,Path.Combine(link,"child"),"pol=clean","rid=absolute-junction"]);Equal(3,rejected.ExitCode);Check(rejected.Stdout.Contains("err=reparse_boundary",StringComparison.Ordinal),rejected.Stdout);Equal("safe",File.ReadAllText(marker));Check(!Directory.Exists(Path.Combine(outside,"child")),"outside mutated");var alive=await RunPaoAsync(["diag","rid=after-junction"]);Equal(0,alive.ExitCode);}finally{if(Directory.Exists(link))Directory.Delete(link);}
}

static async Task ForgedPathRootsAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var src=temp.Dir("src");File.WriteAllText(Path.Combine(src,"new.txt"),"new");var outside=temp.Dir("outside");var marker=Path.Combine(outside,"marker.txt");File.WriteAllText(marker,"safe");var link=Path.Combine(temp.Path,"link");var dst=Path.Combine(link,"child");var junction=await RunProcessAsync(Pwsh(),["-NoProfile","-NonInteractive","-File",JunctionScript(),link,outside]);Equal(0,junction.ExitCode);
    try{using var service=await StartServiceAsync(state);await using var pipe=new NamedPipeClientStream(".",NamedPipeDispatcher.UserPipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Impersonation);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await pipe.ConnectAsync(timeout.Token);var forged=new OperationRequest("cp",new Dictionary<string,string>{{"src",src},{"dst",dst},{"pol","clean"}},"forged-roots",PathRoots:new Dictionary<string,string>{{"src",src},{"dst",dst}});await BinaryProtocol.WriteAsync(pipe,forged,timeout.Token);var receipt=await BinaryProtocol.ReadAsync<OperationReceipt>(pipe,timeout.Token);Equal("reparse_boundary",receipt.Err);Equal("forged-roots",receipt.Rid);Equal("safe",File.ReadAllText(marker));Check(!Directory.Exists(Path.Combine(outside,"child")),"forged roots escaped junction");var alive=await RunPaoAsync(["diag","rid=after-forged-roots"]);Equal(0,alive.ExitCode);}finally{if(Directory.Exists(link))Directory.Delete(link);}
}

static async Task ForgedSystemContextAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("forged-system-state");var dst=Path.Combine(temp.Path,"must-wait-for-tray");using var service=await StartServiceAsync(state,startTray:false);
    await using var pipe=new NamedPipeClientStream(".",NamedPipeDispatcher.UserPipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Impersonation);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await pipe.ConnectAsync(timeout.Token);var forged=new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",dst},{"__execution_context_system","1"}},"forged-system-context");await BinaryProtocol.WriteAsync(pipe,forged,timeout.Token);var receipt=await BinaryProtocol.ReadAsync<OperationReceipt>(pipe,timeout.Token);Equal("waiting_user",receipt.State);Check(!Directory.Exists(dst),"caller-forged System context executed without a tray");using var tray=await StartTrayAsync(state,"forged-system-tray");await WaitRouterStateAsync(state,"forged-system-context","done");Check(Directory.Exists(dst),"ordinary user request did not resume through tray");
}

static async Task BinaryProtocolAsync()
{
    var request = new OperationRequest("diag", new Dictionary<string, string>());
    await using var stream = new MemoryStream(); await BinaryProtocol.WriteAsync(stream, request); stream.Position = 0;
    Equal("diag", (await BinaryProtocol.ReadAsync<OperationRequest>(stream)).Op);
    var bytes = stream.ToArray(); bytes[2] = 1; bytes[3] = 0;
    await using var newer = new MemoryStream(bytes); await ThrowsAsync<InvalidDataException>("protocol_minor_unsupported", () => BinaryProtocol.ReadAsync<OperationRequest>(newer));
}

static Task Phase2Async()
{
    using var temp=new TempRoot(); var file=Path.Combine(temp.Path,"unicode.txt"); var body=Encoding.Unicode.GetBytes("alpha\r\n雪"); File.WriteAllBytes(file,Encoding.Unicode.GetPreamble().Concat(body).ToArray());
    var read=ExtendedOperations.Read(file); Equal("alpha\r\n雪",read["text"]!.ToString()); Equal("utf16le",read["enc"]!.ToString()); Equal(true,(bool)read["bom"]!);
    return Task.CompletedTask;
}

static async Task PowerShellAsync()
{
    using var temp = new TempRoot();
    var script = Path.Combine(temp.Path, "arg test's [x].ps1");
    File.WriteAllText(script, "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Values) [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $Values | ConvertTo-Json -Compress");
    var values = new[] { "space value", "apostrophe's", "\"quotes\"", "$dollar", "`backtick", "[brackets]", "a&b", "雪-λ" };
    var arg = new Dictionary<string, string> { ["src"] = script, ["tmo"] = "30000" };
    for (var i = 0; i < values.Length; i++) arg[$"arg{i}"] = values[i];
    var state=temp.Dir("state");await using(var unconfigured=new AgentOpsEngine(state)){var absent=await unconfigured.ExecuteAsync(new OperationRequest("pwsh",arg));Equal("pwsh_unconfigured",absent.Err);Check(absent.Remedy?.Contains("PowerShell 7",StringComparison.Ordinal)==true,"missing PowerShell remedy");}var registered=await PowerShell7ToolBootstrap.EnsureAsync(state);Check(registered.ExecutablePath==PowerShell7ToolBootstrap.ResolveExecutable()&&registered.ProbeVerified,"registry PowerShell was not probed");var tools=new ToolRecipeOperations(state).Tools;var custom=await tools.RegisterAsync(registered with{DefinitionVersion=2,MaxArguments=42});var preserved=await PowerShell7ToolBootstrap.EnsureAsync(state);Equal(42,preserved.MaxArguments);Equal(custom.ExecutablePath,preserved.ExecutablePath);var windowsPowerShell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");await ThrowsAsync<AgentOpsException>("precondition",()=>tools.RegisterAsync(registered with{DefinitionVersion=3,ExecutablePath=windowsPowerShell,ExecutableSha256=null}));await using var engine = new AgentOpsEngine(state);
    var receipt = await engine.ExecuteAsync(new OperationRequest("pwsh", arg)); Check(receipt.Ok, receipt.Err ?? "pwsh failed");
    var output = receipt.Data!["out"]!.ToString()!.Trim(); var returned = JsonSerializer.Deserialize<string[]>(output)!;
    Equal(values, returned);
    var contextScript=Path.Combine(temp.Path,"execution-context.ps1");
    File.WriteAllText(contextScript,"[Console]::Out.Write($env:PEGASUS_PAO_MUTATION_DEPTH)",new UTF8Encoding(false));
    var context=await engine.ExecuteAsync(new OperationRequest("pwsh",new Dictionary<string,string>{{"src",contextScript},{"tmo","10"}},"execution-context"));
    Check(context.Ok,context.Err??"execution context failed");
    Equal("1",context.Data!["out"]?.ToString());
}

static async Task ReceiptAsync()
{
    using var temp = new TempRoot(); var state = temp.Dir("state"); OperationReceipt first;
    await using (var engine = new AgentOpsEngine(state)) first = await engine.ExecuteAsync(new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(temp.Path, "made") }, "same-rid"));
    await using (var engine = new AgentOpsEngine(state)) { var second = await engine.ExecuteAsync(new OperationRequest("diag", new Dictionary<string, string>(), "same-rid")); Equal(first.Jid, second.Jid); }
    Check(File.Exists(Path.Combine(state, "agentops.db")), "sqlite missing");
}

static async Task ConcurrentRidAsync()
{
    using var temp = new TempRoot(); await using var engine = new AgentOpsEngine(temp.Dir("state"));
    var request = new OperationRequest("mkdir", new Dictionary<string,string> { ["dst"] = Path.Combine(temp.Path, "same") }, "concurrent-rid");
    var receipts = await Task.WhenAll(engine.ExecuteAsync(request), engine.ExecuteAsync(request));
    Equal(receipts[0].Jid, receipts[1].Jid); Equal(receipts[0].State, receipts[1].State);
}

static async Task RestartQueuedAsync()
{
    using var temp = new TempRoot(); var state = temp.Dir("state");await RegisterToolAsync(state,"pwsh",ResolveOnPath("pwsh.exe")); var engine = new AgentOpsEngine(state);
    var first = engine.ExecuteAsync(new OperationRequest("pwsh", new Dictionary<string,string> { ["src"] = DelayScript(), ["arg0"] = "5", ["tmo"] = "20" }, "restart-running"));
    await WaitForStateAsync(state, "restart-running", "running");
    var destination = Path.Combine(temp.Path, "replayed");
    var second = engine.ExecuteAsync(new OperationRequest("mkdir", new Dictionary<string,string> { ["dst"] = destination }, "restart-queued"));
    await WaitForStateAsync(state, "restart-queued", "queued");
    await engine.DisposeAsync(); try { await first; } catch (OperationCanceledException) { } try { await second; } catch (OperationCanceledException) { }
    await using var recovered = new AgentOpsEngine(state);
    var deadline = Stopwatch.StartNew(); while (!Directory.Exists(destination) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
    Check(Directory.Exists(destination), "queued work was not replayed");
    await WaitForStateAsync(state, "restart-running", "canceled"); await WaitForStateAsync(state, "restart-queued", "done");
}

static async Task CancelQueuedAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");await RegisterToolAsync(state,"pwsh",ResolveOnPath("pwsh.exe"));var src=temp.Dir("src");File.WriteAllText(Path.Combine(src,"new.txt"),"new");var dst=temp.Dir("dst");var marker=Path.Combine(dst,"marker.txt");File.WriteAllText(marker,"safe");var engine=new AgentOpsEngine(state);
    var first=engine.ExecuteAsync(new OperationRequest("pwsh",new Dictionary<string,string>{{"src",DelayScript()},{"arg0","2"}},"cancel-blocker"));await WaitForStateAsync(state,"cancel-blocker","running");using var cancel=new CancellationTokenSource();var pending=engine.ExecuteAsync(new OperationRequest("cp",new Dictionary<string,string>{{"src",src},{"dst",dst},{"pol","clean"}},"cancel-clean"),cancel.Token);await WaitForStateAsync(state,"cancel-clean","queued");cancel.Cancel();try{await pending;}catch(OperationCanceledException){}await first;await engine.DisposeAsync();Equal("safe",File.ReadAllText(marker));await using var recovered=new AgentOpsEngine(state);await Task.Delay(250);Equal("safe",File.ReadAllText(marker));await WaitForStateAsync(state,"cancel-clean","canceled");
}

static async Task AliasRebindAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");await RegisterToolAsync(state,"pwsh",Pwsh());var one=temp.Dir("one");var two=temp.Dir("two");var src=Directory.CreateDirectory(Path.Combine(one,"src")).FullName;File.WriteAllText(Path.Combine(src,"new.txt"),"new");var dst1=Directory.CreateDirectory(Path.Combine(one,"dst")).FullName;File.WriteAllText(Path.Combine(dst1,"old.txt"),"old");var dst2=Directory.CreateDirectory(Path.Combine(two,"dst")).FullName;var marker2=Path.Combine(dst2,"marker.txt");File.WriteAllText(marker2,"safe");
    var engine=new AgentOpsEngine(state);Check((await engine.ExecuteAsync(new OperationRequest("ctx",new Dictionary<string,string>{{"register",$"target:{one}"}},"register-one"))).Ok,"register one");var delay=engine.ExecuteAsync(new OperationRequest("pwsh",new Dictionary<string,string>{{"src",DelayScript()},{"arg0","2"}},"alias-blocker"));await WaitForStateAsync(state,"alias-blocker","running");var clean=engine.ExecuteAsync(new OperationRequest("cp",new Dictionary<string,string>{{"src","src"},{"dst","dst"},{"pol","clean"}},"alias-clean",Ws:"target"));await WaitForStateAsync(state,"alias-clean","queued");var rebind=engine.ExecuteAsync(new OperationRequest("ctx",new Dictionary<string,string>{{"register",$"target:{two}"}},"alias-rebind"));await delay;var copied=await clean;var rebound=await rebind;Check(copied.Ok&&rebound.Ok,"alias queue failed");Equal("new",File.ReadAllText(Path.Combine(dst1,"new.txt")));Equal("safe",File.ReadAllText(marker2));await engine.DisposeAsync();await using var recovered=new AgentOpsEngine(state);await Task.Delay(100);Equal("safe",File.ReadAllText(marker2));
}

static async Task CrossWorkspaceAliasRebindAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");await RegisterToolAsync(state,"pwsh",Pwsh());var a=temp.Dir("a");var b=temp.Dir("b");var rebound=temp.Dir("rebound");var src=Directory.CreateDirectory(Path.Combine(a,"src")).FullName;File.WriteAllText(Path.Combine(src,"new.txt"),"new");var dst=Directory.CreateDirectory(Path.Combine(b,"dst")).FullName;File.WriteAllText(Path.Combine(dst,"old.txt"),"old");var reboundDst=Directory.CreateDirectory(Path.Combine(rebound,"dst")).FullName;var marker=Path.Combine(reboundDst,"marker.txt");File.WriteAllText(marker,"safe");
    await using var engine=new AgentOpsEngine(state);Check((await engine.ExecuteAsync(new OperationRequest("ctx",new Dictionary<string,string>{{"register",$"a:{a}"}},"register-a"))).Ok,"register a");Check((await engine.ExecuteAsync(new OperationRequest("ctx",new Dictionary<string,string>{{"register",$"b:{b}"}},"register-b"))).Ok,"register b");var delay=engine.ExecuteAsync(new OperationRequest("pwsh",new Dictionary<string,string>{{"src",DelayScript()},{"arg0","2"}},"cross-blocker"));await WaitForStateAsync(state,"cross-blocker","running");var clean=engine.ExecuteAsync(new OperationRequest("cp",new Dictionary<string,string>{{"src","a:src"},{"dst","b:dst"},{"pol","clean"}},"cross-clean"));await WaitForStateAsync(state,"cross-clean","queued");var rebind=engine.ExecuteAsync(new OperationRequest("ctx",new Dictionary<string,string>{{"register",$"b:{rebound}"}},"rebind-b"));await delay;var copied=await clean;await rebind;Check(copied.Ok,copied.Err??"cross copy failed");Equal("new",File.ReadAllText(Path.Combine(dst,"new.txt")));Equal("safe",File.ReadAllText(marker));
}

static async Task JobStatWaitAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");await RegisterToolAsync(state,"pwsh",Pwsh());await using var engine=new AgentOpsEngine(state);var targetTask=engine.ExecuteAsync(new OperationRequest("pwsh",new Dictionary<string,string>{{"src",DelayScript()},{"arg0","2"}},"wait-target"));await WaitForStateAsync(state,"wait-target","running");var jid=GetJid(state,"wait-target");var watch=Stopwatch.StartNew();var stat=await engine.ExecuteAsync(new OperationRequest("job",new Dictionary<string,string>{{"op","stat"},{"jid",jid}},"stat-request"));Check(watch.Elapsed<TimeSpan.FromSeconds(1),"job stat blocked");Check(stat.Ok,"job stat failed");var waited=await engine.ExecuteAsync(new OperationRequest("job",new Dictionary<string,string>{{"op","wait"},{"jid",jid}},"wait-request"));Check(watch.Elapsed>=TimeSpan.FromSeconds(1),"job wait returned before target terminal");Equal(jid,waited.Jid);Equal("done",waited.State);await targetTask;
}

static async Task LocalJobLogAsync()
{
    using var temp=new TempRoot();
    var state=temp.Dir("state");
    await using var engine=new AgentOpsEngine(state);
    var target=await engine.ExecuteAsync(new OperationRequest("stat",new Dictionary<string,string>{{"src",temp.Path}},"local-log-target"));
    Check(target.Ok,target.Err??"fixture operation failed");
    var log=await engine.ExecuteAsync(new OperationRequest("job",new Dictionary<string,string>{{"op","log"},{"jid",target.Jid},{"max","256"}},"local-log-read"));
    Check(log.Ok,log.Err??"local job_log failed");
    Equal(target.Jid,Convert.ToString(log.Data!["jid"]));
    Equal("done",Convert.ToString(log.Data["state"]));
    Check(!log.Data.ContainsKey("out")&&!log.Data.ContainsKey("err_tail"),"job_log manufactured absent process output");
    var bounded=JobLogProjection.Create(new OperationReceipt(false,"bounded-jid","bounded-rid","failed","run","external_failed",
        Data:new Dictionary<string,object?>{{"exit",23},{"out",new string('x',300)},{"err_tail","failure"}}),256);
    Equal("bounded-jid",Convert.ToString(bounded["jid"]));
    Equal(256,Encoding.UTF8.GetByteCount(Convert.ToString(bounded["out"])!));
    Equal(true,Convert.ToBoolean(bounded["out_tail_truncated"]));
}

static async Task ServiceJobControlAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("job-control-state");using var service=await StartServiceAsync(state,startTray:false);
    var canceledPath=Path.Combine(temp.Path,"must-not-run");var waiting=await RunPaoAsync(["mkdir",canceledPath,"rid=cancel-target"]);Check(Field(waiting.Stdout,"state")=="waiting_user",$"waiting receipt exit={waiting.ExitCode} out={waiting.Stdout} err={waiting.Stderr}");var canceled=await RunPaoAsync(["job","cancel",Field(waiting.Stdout,"jid"),"rid=cancel-control"]);Check(canceled.ExitCode==0,$"cancel exit={canceled.ExitCode} out={canceled.Stdout} err={canceled.Stderr}");Equal("canceled",Field(canceled.Stdout,"state"));Equal("true",Field(canceled.Stdout,"cancel_applied"));
    var bodyTarget=Path.Combine(temp.Path,"body.txt");var body=await RunPaoAsync(["wr",bodyTarget,"rid=body-cancel","--stdin"],"ephemeral-body");var bodyCancel=await RunPaoAsync(["job","cancel",Field(body.Stdout,"jid")]);Equal(0,bodyCancel.ExitCode);Equal("canceled",Field(bodyCancel.Stdout,"state"));Equal("true",Field(bodyCancel.Stdout,"cancel_applied"));var bodyRetry=await RunPaoAsync(["job","retry",Field(body.Stdout,"jid"),"rid=body-retry"]);Equal(3,bodyRetry.ExitCode);Check(!File.Exists(bodyTarget),"body-bearing job replayed without its body");Check(!StateContainsAscii(state,"ephemeral-body"),"body leaked into durable job-control state");
    using var tray=await StartTrayAsync(state,"job-control");await Task.Delay(300);Check(!Directory.Exists(canceledPath),"canceled queued mutation executed");

    var src=Path.Combine(temp.Path,"retry-src.txt");var dst=Path.Combine(temp.Path,"retry-dst.txt");File.WriteAllText(src,"new",Encoding.UTF8);File.WriteAllText(dst,"old",Encoding.UTF8);var failed=await RunPaoAsync(["cp",src,dst,"rid=retry-source"]);Equal(3,failed.ExitCode);File.Delete(dst);var retried=await RunPaoAsync(["job","retry",Field(failed.Stdout,"jid"),"rid=retry-fresh"]);Equal("waiting_user",Field(retried.Stdout,"state"));var retryJid=Field(retried.Stdout,"jid");var duplicate=await RunPaoAsync(["job","retry",Field(failed.Stdout,"jid"),"rid=retry-fresh"]);Equal(retryJid,Field(duplicate.Stdout,"jid"));var completed=await RunPaoAsync(["job","wait",retryJid]);Equal(0,completed.ExitCode);Equal("new",File.ReadAllText(dst,Encoding.UTF8));

    var script=Path.Combine(temp.Path,"job log Ω.ps1");File.WriteAllText(script,"[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Out.Write('stdout-雪'); [Console]::Error.Write('stderr-λ'); exit 23",new UTF8Encoding(false));var external=await RunPaoAsync(["pwsh",script,"rid=log-source"]);Equal(6,external.ExitCode);var log=await RunPaoAsync(["job","log",Field(external.Stdout,"jid"),"max=256"]);Equal(0,log.ExitCode);Check(Uri.UnescapeDataString(Field(log.Stdout,"out")).Contains("stdout-雪",StringComparison.Ordinal),log.Stdout);Check(Uri.UnescapeDataString(Field(log.Stdout,"err_tail")).Contains("stderr-λ",StringComparison.Ordinal),log.Stdout);
    var active=RunPaoAsync(["pwsh",DelayScript(),"rid=active-cancel","--","10"]);await WaitForRidAsync(state,"active-cancel");await WaitRouterStateAsync(state,"active-cancel","dispatching");var activeCancel=await RunPaoAsync(["job","cancel",RouterJid(state,"active-cancel"),"rid=active-cancel-control"]);Equal(7,activeCancel.ExitCode);Equal("recovery_required",Field(activeCancel.Stdout,"state"));Check(activeCancel.Stdout.Contains("partial=1",StringComparison.Ordinal),activeCancel.Stdout);var activeResult=await active;Equal("recovery_required",Field(activeResult.Stdout,"state"));
}

static async Task TrayPauseResumeAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("pause-state");var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,3);var calls=0;var dst=Path.Combine(temp.Path,"after-resume");
    await using(var first=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,(_,_,request,_)=>{Interlocked.Increment(ref calls);Directory.CreateDirectory(request.Arg["dst"]);return Task.FromResult(new OperationReceipt(true,"inner",request.Rid!,"done",request.Op));}))
    {
        Check((await first.RegisterTrayAsync(new TrayRegistration(owner,3,"pause-tray"),peer)).Ok,"tray registration failed");Check(first.SetTrayPaused(true,peer).Ok,"tray pause rejected");var queued=await first.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",dst}},"paused-job"),peer,CancellationToken.None);Equal("waiting_user",queued.State);Equal(true,Convert.ToBoolean(queued.Data!["approval_required"]));Equal("tray",queued.Data["approval_via"]?.ToString());await Task.Delay(150);Equal(0,calls);
    }
    await using(var restarted=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,(_,_,request,_)=>{Interlocked.Increment(ref calls);Directory.CreateDirectory(request.Arg["dst"]);return Task.FromResult(new OperationReceipt(true,"inner",request.Rid!,"done",request.Op));}))
    {
        Check((await restarted.RegisterTrayAsync(new TrayRegistration(owner,3,"pause-tray-2"),peer)).Ok,"restart tray registration failed");await Task.Delay(150);Equal(0,calls);Check(restarted.SetTrayPaused(false,peer).Ok,"tray resume rejected");await WaitRouterStateAsync(state,"paused-job","done");Equal(1,calls);Check(Directory.Exists(dst),"resumed job did not execute");
    }
}

static async Task TypedAdapterArgumentsAsync()
{
    var capture=new CaptureDispatcher();using var output=new StringWriter();using var error=new StringWriter();Equal(0,await PaoProgram.RunCliAsync(["dotnet","build","demo.csproj","cfg=Release"],capture,output,error,CancellationToken.None));Equal("dotnet",capture.Last!.Op);Equal("build",capture.Last.Arg["op"]);Equal("demo.csproj",capture.Last.Arg["src"]);Equal("Release",capture.Last.Arg["cfg"]);Check(!capture.Last.Arg.ContainsKey("arg0"),"dotnet target leaked into passthrough argv");
    Equal(0,await PaoProgram.RunCliAsync(["git","status"],capture,output,error,CancellationToken.None));Equal("git",capture.Last!.Op);Equal("status",capture.Last.Arg["op"]);
    using var temp=new TempRoot();var project=Path.Combine(temp.Path,"demo.csproj");await File.WriteAllTextAsync(project,"fixture");var argsPath=Path.Combine(temp.Path,"actual-args.json");using var service=await StartServiceAsync(temp.Dir("state"),FakeToolExe(),argsPath);var actual=await RunPaoAsync(["dotnet","build",project,"cfg=Release","rid=fake-dotnet"]);Equal(0,actual.ExitCode);var argv=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argsPath))!;Equal(new[]{"build",project,"--configuration","Release"},argv);Equal("dotnet_build",Field(actual.Stdout,"op"));
}

static async Task ExternalOutputBoundedAsync()
{
    using var temp=new TempRoot();var script=Path.Combine(temp.Path,"output.ps1");File.WriteAllText(script,"[Console]::Out.Write(('o' * 70000)); [Console]::Error.Write(('e' * 70000))");var pwsh=Pwsh();var result=await ProcessRunner.RunAsync(pwsh,["-NoProfile","-NonInteractive","-File",script],TimeSpan.FromSeconds(10),temp.Path,ProcessEnvironment.Replace(MinimumToolEnvironment(pwsh)),CancellationToken.None);Equal(0,result.ExitCode);Equal(65536,result.Stdout.Length);Equal(65536,result.Stderr.Length);Check(result.StdoutTruncated&&result.StderrTruncated,"missing truncation markers");
}

static async Task Phase2CliBodyFormsAsync()
{
    var capture=new CaptureDispatcher();using var output=new StringWriter();using var error=new StringWriter();var path=@"C:\work space\ユニコード & equals=file.txt";
    Equal(0,await PaoProgram.RunCliAsync(["wr",path,"--stdin"],capture,output,error,CancellationToken.None,new StringReader("full & body\nΩ")));Equal("wr",capture.Last!.Op);Equal(path,capture.Last.Arg["dst"]);Equal("full & body\nΩ",capture.Last.Body);
    Equal(0,await PaoProgram.RunCliAsync(["edit",path,"2","4","--stdin","enc=utf8"],capture,output,error,CancellationToken.None,new StringReader("replacement chunk Ω")));Equal("edit",capture.Last!.Op);Equal(path,capture.Last.Arg["dst"]);Equal("2",capture.Last.Arg["line"]);Equal("3",capture.Last.Arg["count"]);Equal("utf8",capture.Last.Arg["enc"]);Equal("replacement chunk Ω",capture.Last.Body);
    Equal(2,await PaoProgram.RunCliAsync(["edit",path,"4","2","--stdin"],capture,output,error,CancellationToken.None,new StringReader("invalid range")));
    Equal(0,await PaoProgram.RunCliAsync(["patch","--stdin","ws=demo"],capture,output,error,CancellationToken.None,new StringReader("--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n")));Equal("patch",capture.Last!.Op);Check(!capture.Last.Arg.ContainsKey("dst"),"public patch accepted dst");Check(capture.Last.Body!.Contains("+++ b/file.txt",StringComparison.Ordinal),"patch body lost");
    Equal(0,await PaoProgram.RunCliAsync(["text","replace",path,"OLD = Ω","NEW & ` [雪]"],capture,output,error,CancellationToken.None));Equal("wr",capture.Last!.Op);Equal("replace",capture.Last.Arg["op"]);Equal("OLD = Ω",capture.Last.Arg["old"]);Equal("NEW & ` [雪]",capture.Last.Body);
    Equal(0,await PaoProgram.RunCliAsync(["ctx","add","demo",@"C:\work space\root"],capture,output,error,CancellationToken.None));Equal(@"demo:C:\work space\root",capture.Last!.Arg["register"]);

    using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(capture,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="line-edit-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=new{op="edit",dst=path,line=2,count=3,enc="utf8",text="MCP chunk 雪"}}}));var response=await writer.Response.WaitAsync(TimeSpan.FromSeconds(5));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var document=JsonDocument.Parse(response);Check(document.RootElement.TryGetProperty("result",out _),response);Equal("edit",capture.Last!.Op);Equal("2",capture.Last.Arg["line"]);Equal("3",capture.Last.Arg["count"]);Equal("MCP chunk 雪",capture.Last.Body);
}

static async Task LineEditBrokerAsync()
{
    using var temp=new TempRoot();var serviceState=temp.Dir("edit-service");var trayState=temp.Dir("edit-tray");var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,3);var file=Path.Combine(temp.Path,"broker edit Ω.txt");await File.WriteAllTextAsync(file,"one\r\ntwo\r\nthree\r\nfour\r\n",new UnicodeEncoding(false,true));var body="TWO Ω\nTHREE from broker";
    await using var engine=new AgentOpsEngine(trayState);await using var broker=new AuthenticatedServiceBroker(serviceState,owner,new RootCodeVerifier(Path.Combine(serviceState,"root.verifier")),false,(_,_,request,ct)=>engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct));Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"edit-direct"),peer)).Ok,"edit tray rejected");var receipt=await broker.HandleUserAsync(new OperationRequest("edit",new Dictionary<string,string>{{"dst",file},{"line","2"},{"count","2"}},"edit-broker",Cwd:temp.Path,Body:body),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));Check(receipt.Ok,receipt.Err??"line edit failed");Equal("one\r\nTWO Ω\r\nTHREE from broker\r\nfour\r\n",await File.ReadAllTextAsync(file,Encoding.Unicode));Check(!StateContainsAscii(serviceState,body)&&!StateContainsAscii(trayState,body),"line edit body persisted");
}

static async Task TextContextEncodingHelpAsync()
{
    var capture=new CaptureDispatcher();using var output=new StringWriter();using var error=new StringWriter();var path=@"C:\work space\context Ω.txt";
    Equal(0,await PaoProgram.RunCliAsync(["read-lines",path,"line=7","count=25","enc=utf8"],capture,output,error,CancellationToken.None));Equal("read_lines",capture.Last!.Op);Equal(path,capture.Last.Arg["src"]);Equal("7",capture.Last.Arg["line"]);Equal("25",capture.Last.Arg["count"]);
    Equal(0,await PaoProgram.RunCliAsync(["encoding","inspect",path,"enc=codepage:1252"],capture,output,error,CancellationToken.None));Equal("encoding",capture.Last!.Op);Equal("inspect",capture.Last.Arg["op"]);Equal(path,capture.Last.Arg["src"]);
    Equal(0,await PaoProgram.RunCliAsync(["encoding","convert",path,@"C:\work space\converted Ω.txt","enc=utf8bom","sha="+new string('a',64)],capture,output,error,CancellationToken.None));Equal("convert",capture.Last!.Arg["op"]);Equal("utf8bom",capture.Last.Arg["enc"]);
    Equal(2,await PaoProgram.RunCliAsync(["read-lines",path,"line=0"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["encoding","convert",path,@"C:\work space\out.txt"],capture,output,error,CancellationToken.None));
    var beforeHelp=capture.Last;output.GetStringBuilder().Clear();Equal(0,await PaoProgram.RunCliAsync(["help","encoding_convert"],capture,output,error,CancellationToken.None));Check(ReferenceEquals(beforeHelp,capture.Last),"help unexpectedly contacted the dispatcher");var helped=output.ToString();Check(helped.Contains("Allowed:",StringComparison.Ordinal)&&helped.Contains("src",StringComparison.Ordinal)&&helped.Contains("enc",StringComparison.Ordinal),helped);Equal(2,await PaoProgram.RunCliAsync(["describe","encoding_convert"],capture,output,error,CancellationToken.None));

    var lineResponse=await CallAsync(capture,new{op="read_lines",src=path,line=2,count=3,enc="utf8",detail=true});Check(lineResponse.TryGetProperty("result",out _),lineResponse.ToString());Equal("read_lines",capture.Last!.Op);Equal("2",capture.Last.Arg["line"]);Equal(true,capture.Last.Detail);
    var helpResponse=await CallAsync(capture,new{op="help",name="read_lines"});var localReceipt=helpResponse.GetProperty("result").GetProperty("structuredContent");Equal("local",localReceipt.GetProperty("jid").GetString());Equal("help",localReceipt.GetProperty("op").GetString());Equal(true,localReceipt.GetProperty("data").GetProperty("local").GetBoolean());Check(localReceipt.GetProperty("data").GetProperty("constraints").GetString()!.Contains("one-based",StringComparison.Ordinal),localReceipt.ToString());
    var invalidResponse=await CallAsync(capture,new{op="read_lines",src=path,line=0,count=1});Equal(-32602,invalidResponse.GetProperty("error").GetProperty("code").GetInt32());Check(invalidResponse.GetProperty("error").GetProperty("message").GetString()!.Contains("name=read_lines",StringComparison.Ordinal),invalidResponse.ToString());

    using var temp=new TempRoot();var serviceState=temp.Dir("text-contract-service");var trayState=temp.Dir("text-contract-tray");var workspace=temp.Dir("text contract workspace");new WorkspaceRegistry(new AgentOpsPaths(serviceState)).Register("text-contract",workspace);var source=Path.Combine(workspace,"source Ω.txt");await File.WriteAllBytesAsync(source,[0xFF,0xFE,..new UnicodeEncoding(false,false,true).GetBytes("one\r\ntwo Ω\r\nthree\r\n")]);var destination=Path.Combine(workspace,"converted utf8.txt");var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,3);
    await using var engine=new AgentOpsEngine(trayState,directOnly:true);await using var broker=new AuthenticatedServiceBroker(serviceState,owner,new RootCodeVerifier(Path.Combine(serviceState,"root.verifier")),false,(_,_,request,ct)=>engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct));Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"text-contract-tray"),peer)).Ok,"text contract tray rejected");
    var lines=await broker.HandleUserAsync(new OperationRequest("read_lines",new Dictionary<string,string>{{"src","source Ω.txt"},{"line","2"},{"count","1"}},"context-read",Ws:"text-contract",Cwd:null),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));Check(lines.Ok,lines.Err??"read lines failed");Equal(3,lines.Data!["total"] is JsonElement totalJson?totalJson.GetInt32():Convert.ToInt32(lines.Data["total"]));var lineEntries=lines.Data["entries"] is JsonElement entriesJson?entriesJson:JsonSerializer.SerializeToElement(lines.Data["entries"]);Equal("two Ω",lineEntries[0].GetProperty("text").GetString());
    var inspect=await broker.HandleUserAsync(new OperationRequest("encoding",new Dictionary<string,string>{{"op","inspect"},{"src","source Ω.txt"}},"encoding-inspect",Ws:"text-contract",Cwd:null),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));Check(inspect.Ok,inspect.Err??"inspect failed");Check(!inspect.Data!.ContainsKey("text"),"service encoding inspection exposed content");Equal("utf16le",inspect.Data["enc"]?.ToString());
    var convert=await broker.HandleUserAsync(new OperationRequest("encoding",new Dictionary<string,string>{{"op","convert"},{"src","source Ω.txt"},{"dst","converted utf8.txt"},{"enc","utf8"}},"encoding-convert",Ws:"text-contract",Cwd:null),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));Check(convert.Ok,convert.Err??"convert failed");Equal("one\r\ntwo Ω\r\nthree\r\n",await File.ReadAllTextAsync(destination,new UTF8Encoding(false,true)));

    static async Task<JsonElement> CallAsync(IOperationDispatcher dispatcher,object arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="context-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(5));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task CandidateHelpTranscriptAsync()
{
    var input=string.Join('\n',
        JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="candidate-contract",version="1"}}}),
        JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}),
        JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=new{op="help",name="job_log",output="agent"}}}))+'\n';
    var result=await RunProcessAsync(Dotnet(),[PaoDll(),"mcp"],input,200);
    Equal(0,result.ExitCode);
    Equal(string.Empty,result.Stderr.Trim());
    var lines=result.Stdout.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);
    Equal(2,lines.Length);
    using var response=JsonDocument.Parse(lines[1]);
    var tool=response.RootElement.GetProperty("result");
    Equal(false,tool.GetProperty("isError").GetBoolean());
    var text=tool.GetProperty("content")[0].GetProperty("text").GetString()!;
    Check(text.StartsWith("OK local\n",StringComparison.Ordinal),text);
    using var help=JsonDocument.Parse(text[(text.IndexOf('\n')+1)..]);
    Check(help.RootElement.GetProperty("mcp_example").GetString()!.Contains("\"op\":\"job_log\"",StringComparison.Ordinal),text);
    Check(!result.Stdout.Contains("\\u0022",StringComparison.Ordinal) &&
        !result.Stdout.Contains("\\u0027",StringComparison.Ordinal),
        "The actual candidate MCP help transcript unnecessarily escaped quotation marks.");
    Console.WriteLine("EVIDENCE actual built PAO MCP initialize/help: valid readable JSON, two protocol lines, no stderr; host-local help only.");
}

static async Task CompactParserAndExitTaxonomyAsync()
{
    var capture=new CaptureDispatcher();using var output=new StringWriter();using var error=new StringWriter();
    foreach(var alias in new[]{"/?","/help","?"})
    {
        output.GetStringBuilder().Clear();
        Equal(0,await PaoProgram.RunCliAsync([alias],capture,output,error,CancellationToken.None));
        Check(output.ToString().Contains("Pegasus PAO",StringComparison.Ordinal)&&output.ToString().Contains("--json",StringComparison.Ordinal),output.ToString());
    }
    output.GetStringBuilder().Clear();
    Equal(0,await PaoProgram.RunCliAsync(["help"],capture,output,error,CancellationToken.None));
    Check(output.ToString().Contains("Pegasus PAO",StringComparison.Ordinal)&&output.ToString().Contains("pao help <operation>",StringComparison.Ordinal),output.ToString());
    output.GetStringBuilder().Clear();
    Equal(0,await PaoProgram.RunCliAsync(["help","nsis_build"],capture,output,error,CancellationToken.None));
    Check(output.ToString().Contains("PAO nsis_build",StringComparison.Ordinal)&&output.ToString().Contains("job log",StringComparison.Ordinal),output.ToString());
    output.GetStringBuilder().Clear();
    Equal(0,await PaoProgram.RunCliAsync(["read-lines","/?"],capture,output,error,CancellationToken.None));
    Check(output.ToString().Contains("PAO read_lines",StringComparison.Ordinal)&&output.ToString().Contains("pao read-lines",StringComparison.Ordinal),output.ToString());
    output.GetStringBuilder().Clear();
    Equal(0,await PaoProgram.RunCliAsync(["dotnet","build","/?"],capture,output,error,CancellationToken.None));
    Check(output.ToString().Contains("PAO dotnet_build",StringComparison.Ordinal)&&output.ToString().Contains("Required: src",StringComparison.Ordinal),output.ToString());
    Equal(0,await PaoProgram.RunCliAsync(["cp","src=one","dst=two","pol=clean"],capture,output,error,CancellationToken.None));Equal("one",capture.Last!.Arg["src"]);Equal("two",capture.Last.Arg["dst"]);
    Equal(0,await PaoProgram.RunCliAsync(["zip","op=create","src=dir","dst=out.zip"],capture,output,error,CancellationToken.None));Equal("create",capture.Last!.Arg["op"]);
    Equal(0,await PaoProgram.RunCliAsync(["job","op=wait","jid=J42"],capture,output,error,CancellationToken.None));Equal("J42",capture.Last!.Arg["jid"]);
    foreach(var jobOp in new[]{"cancel","retry","log"}){Equal(0,await PaoProgram.RunCliAsync(["job",jobOp,"J42"],capture,output,error,CancellationToken.None));Equal(jobOp,capture.Last!.Arg["op"]);Equal("J42",capture.Last.Arg["jid"]);}
    Equal(0,await PaoProgram.RunCliAsync(["job","log","J42","max=256"],capture,output,error,CancellationToken.None));Equal("256",capture.Last!.Arg["max"]);
    Equal(0,await PaoProgram.RunCliAsync(["run","demo","tmo=300"],capture,output,error,CancellationToken.None));Equal("300",capture.Last!.Arg["tmo"]);
    Equal(0,await PaoProgram.RunCliAsync(["stat","literal:src=name"],capture,output,error,CancellationToken.None));Check(capture.Last!.Arg["src"].EndsWith("src=name",StringComparison.Ordinal),"literal equals path was not preserved");
    Equal(0,await PaoProgram.RunCliAsync(["ctx","ls"],capture,output,error,CancellationToken.None));Equal("ls",capture.Last!.Arg["op"]);Equal(0,await PaoProgram.RunCliAsync(["ctx","cur"],capture,output,error,CancellationToken.None));Equal(0,await PaoProgram.RunCliAsync(["net","op=list"],capture,output,error,CancellationToken.None));
    Equal(2,await PaoProgram.RunCliAsync(["net","op=list","port=oops"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["rm","x","permanent=tru"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["ls",".","max=49"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["run","demo","tmo=oops"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["proc","stop","1","start","extra"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["job","op=wait"],capture,output,error,CancellationToken.None));Equal(2,await PaoProgram.RunCliAsync(["job","log","J42","max=255"],capture,output,error,CancellationToken.None));
    foreach(var (code,expected) in new[]{("invalid",2),("encoding_required",2),("not_found",3),("collision",3),("precondition",3),("reentrant_mutation",3),("reparse_boundary",3),("root_target",3),("pid_reused",3),("access",4),("auth",4),("timeout",5),("cancelled",5),("canceled",5),("external_failed",6),("process_host_runtime",6),("svc_down",6),("user_host_unavailable",6),("recovery_required",7)}){var receipt=new OperationReceipt(false,"j","r","failed","x",code);Equal(expected,receipt.ExitCode);Check(!string.IsNullOrWhiteSpace(receipt.Remedy),$"missing remedy for {code}");}Equal(7,new OperationReceipt(false,"j","r","attention","x","internal",Partial:true).ExitCode);
}

static async Task UnixCompatibilityParserAsync()
{
    var capture=new CaptureDispatcher();using var output=new StringWriter();using var error=new StringWriter();
    Equal(0,await PaoProgram.RunCliAsync(["ux","grep","-R","-n","--include=*.cs","TODO","src"],capture,output,error,CancellationToken.None));Equal("search",capture.Last!.Op);Equal("TODO",capture.Last.Arg["pattern"]);Equal("src",capture.Last.Arg["src"]);Equal("True",capture.Last.Arg["recur"]);Equal("*.cs",capture.Last.Arg["include"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","find",".","-type","f","-name","*.json"],capture,output,error,CancellationToken.None));Equal("find",capture.Last!.Op);Equal("file",capture.Last.Arg["type"]);Equal("*.json",capture.Last.Arg["pattern"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","tail","-n","25","app.log"],capture,output,error,CancellationToken.None));Equal("slice",capture.Last!.Op);Equal("tail",capture.Last.Arg["mode"]);Equal("25",capture.Last.Arg["count"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","zip","-r","out.zip","source"],capture,output,error,CancellationToken.None));Equal("zip",capture.Last!.Op);Equal("create",capture.Last.Arg["op"]);Equal("source",capture.Last.Arg["src"]);Equal("out.zip",capture.Last.Arg["dst"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","sort","-f","items.txt"],capture,output,error,CancellationToken.None));Equal("lines",capture.Last!.Op);Equal("sort",capture.Last.Arg["op"]);Equal("True",capture.Last.Arg["ignore_case"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","uniq","items.txt"],capture,output,error,CancellationToken.None));Equal("uniq",capture.Last!.Arg["op"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","du","-sh","."],capture,output,error,CancellationToken.None));Equal("disk",capture.Last!.Op);Equal("du",capture.Last.Arg["op"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","df","-h"],capture,output,error,CancellationToken.None));Equal("df",capture.Last!.Arg["op"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","ps","aux"],capture,output,error,CancellationToken.None));Equal("proc",capture.Last!.Op);Equal("ls",capture.Last.Arg["op"]);
    Equal(0,await PaoProgram.RunCliAsync(["ux","kill","42","2026-08-12T01:02:03.0000000Z"],capture,output,error,CancellationToken.None));Equal("stop",capture.Last!.Arg["op"]);Equal("42",capture.Last.Arg["pid"]);
    output.GetStringBuilder().Clear();Equal(2,await PaoProgram.RunCliAsync(["ux","kill","42"],capture,output,error,CancellationToken.None));Check(output.ToString().Contains("err=invalid",StringComparison.Ordinal)&&output.ToString().Contains("remedy=",StringComparison.Ordinal),output.ToString());
    Equal(0,await PaoProgram.RunCliAsync(["ux","cat","/c/Program Files/Pegasus AgentOps/file.txt"],capture,output,error,CancellationToken.None));Equal(@"C:\Program Files\Pegasus AgentOps\file.txt",capture.Last!.Arg["src"]);
    output.GetStringBuilder().Clear();Equal(2,await PaoProgram.RunCliAsync(["ux","awk","{print $1}","file"],capture,output,error,CancellationToken.None));Check(output.ToString().Contains("err=unsupported_command",StringComparison.Ordinal),output.ToString());Check(output.ToString().Contains("remedy=",StringComparison.Ordinal),output.ToString());
    output.GetStringBuilder().Clear();Equal(2,await PaoProgram.RunCliAsync(["ux","grep","--perl-regexp","x","."],capture,output,error,CancellationToken.None));Check(output.ToString().Contains("err=unsupported_option",StringComparison.Ordinal),output.ToString());
}

static Task UnixNativeOperationsAsync()
{
    using var temp=new TempRoot();var root=temp.Dir("ux root 雪 &");var nested=Directory.CreateDirectory(Path.Combine(root,"nested")).FullName;var one=Path.Combine(root,"one.cs");var two=Path.Combine(nested,"two.txt");File.WriteAllText(one,"alpha\nTODO first\nomega",new UTF8Encoding(false));File.WriteAllText(two,"TODO second\nlast",new UTF8Encoding(false));
    var found=CompatibilityOperations.Find(root,"*.cs","file",20);Equal(1,(int)found["matched"]!);
    var searched=CompatibilityOperations.Search(root,"TODO",true,false,true,null,20);Equal(2,(int)searched["matched"]!);var bounded=CompatibilityOperations.Search(root,"TODO",true,false,true,null,1);Equal(true,(bool)bounded["truncated"]!);Equal(1,((IEnumerable<object>)bounded["matches"]!).Count());
    var tail=CompatibilityOperations.Slice(one,"tail",2,null);Check(((string)tail["text"]!).Contains("TODO first",StringComparison.Ordinal),"tail lost expected line");
    var count=CompatibilityOperations.Count(one);Equal(3,(int)count["lines"]!);
    var clone=Path.Combine(root,"clone.cs");File.Copy(one,clone);var same=CompatibilityOperations.Diff(one,clone,20);Equal(true,(bool)same["equal"]!);File.AppendAllText(clone,"\nchanged");var changed=CompatibilityOperations.Diff(one,clone,20);Check((int)changed["changed"]!>0,"diff missed change");
    var path=CompatibilityOperations.PathInfo(one,"basename");Equal("one.cs",path["path"]?.ToString());
    var lineFile=Path.Combine(root,"lines.txt");File.WriteAllText(lineFile,"beta\nAlpha\nalpha\nalpha",new UTF8Encoding(false));var sorted=CompatibilityOperations.TransformLines(lineFile,"sort",true);Check(((string)sorted["text"]!).StartsWith("Alpha",StringComparison.Ordinal),"sort order incorrect");var unique=CompatibilityOperations.TransformLines(lineFile,"uniq",true);Equal(2,(int)unique["lines"]!);
    var usage=CompatibilityOperations.Disk(root,"du");Check((long)usage["bytes"]!>0,"du reported no bytes");Check((int)usage["files"]!>=4,"du file count incorrect");var volume=CompatibilityOperations.Disk(root,"df");Check((long)volume["bytes"]!>(long)volume["free"]!,"df capacity incorrect");return Task.CompletedTask;
}

static async Task UnixServiceEquivalenceAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("ux service state");var root=temp.Dir("ux service workspace 雪 &");var file=Path.Combine(root,"sample & 雪.txt");File.WriteAllText(file,"alpha\nTODO service\nomega",new UTF8Encoding(false));using var service=await StartServiceAsync(state);
    var cli=await RunPaoAsync(["ux","grep","-R","-n","-F","TODO",root]);Equal(0,cli.ExitCode);Equal(1L,LongField(cli.Stdout,"matched"));
    var mcp=string.Join('\n',"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"ux-test\",\"version\":\"1\"}}}","{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",$"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"pao\",\"arguments\":{{\"op\":\"search\",\"src\":{JsonSerializer.Serialize(root)},\"pattern\":\"TODO\",\"recur\":true,\"fmt\":\"literal\",\"rid\":\"ux-mcp-search\",\"output\":\"structured\"}}}}}}")+"\n";
    var result=await RunPaoAsync(["mcp"],mcp);Equal(0,result.ExitCode);var documents=result.Stdout.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonDocument.Parse(line)).ToArray();try{var response=documents.Single(x=>x.RootElement.TryGetProperty("id",out var id)&&id.GetInt32()==2);var structured=response.RootElement.GetProperty("result").GetProperty("structuredContent");Equal(true,structured.GetProperty("ok").GetBoolean());Equal(1,structured.GetProperty("data").GetProperty("matched").GetInt32());}finally{foreach(var document in documents)document.Dispose();}
    var unsupported=await RunPaoAsync(["ux","awk","{print $1}",file]);Equal(2,unsupported.ExitCode);Check(unsupported.Stdout.Contains("err=unsupported_command",StringComparison.Ordinal),unsupported.Stdout);
}

static async Task ServiceEphemeralBodyAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("body-state");var secret="body Ω & $ ` [x] "+Guid.NewGuid().ToString("N");var dst=Path.Combine(temp.Path,"body file 雪 &.txt");
    using(var service=await StartServiceAsync(state,startTray:false)){var waiting=await RunPaoAsync(["wr",dst,"rid=body-live","--stdin"],secret);Equal("waiting_user",Field(waiting.Stdout,"state"));var editFile=Path.Combine(temp.Path,"edit body 雪 &.txt");await File.WriteAllTextAsync(editFile,"one\r\ntwo\r\nthree\r\nfour\r\n",new UnicodeEncoding(false,true));var edited=await RunPaoAsync(["edit",editFile,"2","3","rid=edit-live","--stdin"],secret+"\nchunk");Equal("waiting_user",Field(edited.Stdout,"state"));Check(!StateContainsAscii(state,secret),"body persisted before tray");using var tray=await StartTrayAsync(state,"body-live");await WaitRouterStateAsync(state,"body-live","done");await WaitRouterStateAsync(state,"edit-live","done");Equal(secret,await File.ReadAllTextAsync(dst,Encoding.UTF8));Equal("one\r\n"+secret+"\r\nchunk\r\nfour\r\n",await File.ReadAllTextAsync(editFile,Encoding.Unicode));Check(!StateContainsAscii(state,secret),"body persisted after terminal");}
    var dst2=Path.Combine(temp.Path,"restart.txt");using(var service=await StartServiceAsync(state,startTray:false)){var waiting=await RunPaoAsync(["wr",dst2,"rid=body-restart","--stdin"],secret+"restart");Equal("waiting_user",Field(waiting.Stdout,"state"));}
    using(var restarted=await StartServiceAsync(state,startTray:false)){await WaitRouterStateAsync(state,"body-restart","recovery_required");Check(!File.Exists(dst2),"body-required job replayed after restart");Check(!StateContainsAscii(state,secret+"restart"),"restart body persisted");}
}

static async Task WorkspaceRootRemoveAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("rm-state");var workspace=temp.Dir("rm-workspace");using var service=await StartServiceAsync(state);Equal(0,(await RunPaoAsync(["ctx","add","rooted",workspace])).ExitCode);var denied=await RunPaoAsync(["rm",workspace,"ws=rooted","rid=rm-root"]);Equal(3,denied.ExitCode);Check(Directory.Exists(workspace),"workspace root removed");
}

static async Task NoWorkspaceCallerCwdAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("cwd-root-state");var workspace=temp.Dir("cwd workspace 雪 &");using var service=await StartServiceAsync(state,startTray:false);Equal(0,(await RunPaoAsync(["ctx","add","cwdroot",workspace])).ExitCode);var current=await RunPaoFromAsync(workspace,["ctx","cur"]);Equal(0,current.ExitCode);var waiting=await RunPaoFromAsync(workspace,["rm",".","rid=no-ws-root-rm"]);Check(Field(waiting.Stdout,"state")=="waiting_user",waiting.Stdout);var frozen=ReadRouterRequest(state,"no-ws-root-rm");Check(frozen.PathRoots?.TryGetValue("dst",out var root)==true&&StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(root),Path.TrimEndingDirectorySeparator(workspace)),"caller-cwd workspace root admission was not persisted");using var tray=await StartTrayAsync(state,"cwd-root");await WaitRouterStateAsync(state,"no-ws-root-rm","failed");var denied=await RunPaoAsync(["job","stat",Field(waiting.Stdout,"jid")]);Equal(3,denied.ExitCode);Check(Directory.Exists(workspace),"caller-cwd workspace root removed");
}

static async Task TransportDisconnectAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("disconnect-state");var dst=Path.Combine(temp.Path,"after disconnect");using var service=await StartServiceAsync(state);var first=RunPaoAsync(["pwsh",DelayScript(),"rid=disconnect-a","--","2"]);await WaitForRidAsync(state,"disconnect-a");await using(var pipe=new NamedPipeClientStream(".",NamedPipeDispatcher.UserPipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Impersonation)){await pipe.ConnectAsync(2000);await BinaryProtocol.WriteAsync(pipe,new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",dst}},"disconnect-b"));}await first;await WaitRouterStateAsync(state,"disconnect-b","done");Check(Directory.Exists(dst),"disconnected admitted mutation did not execute");
}

static async Task BrokerRouteLossAndForeignAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("broker-boundaries");var owner=LocalIdentity.Current().Sid;var other="S-1-5-21-1064206094-724938797-970556320-424242";var calls=0;
    await using(var broker=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,(_,_,request,_)=>Task.FromResult(new OperationReceipt(false,"x",request.Rid!,"failed",request.Op,"svc_down"))))
    {Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"lost"),new LocalPeer(owner,3))).Ok,"fixture tray rejected");var receipt=await broker.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"waiting")}},"route-lost"),new LocalPeer(owner,3),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));Equal("waiting_user",receipt.State);}
    var foreignState=temp.Dir("foreign-state");await using(var first=new AuthenticatedServiceBroker(foreignState,other,new RootCodeVerifier(Path.Combine(foreignState,"root.verifier")))){var queued=await first.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"foreign")}},"foreign-row"),new LocalPeer(other,3),CancellationToken.None);Equal("waiting_user",queued.State);}
    await using(var replacement=new AuthenticatedServiceBroker(foreignState,owner,new RootCodeVerifier(Path.Combine(foreignState,"root.verifier")),false,(_,_,request,_)=>{Interlocked.Increment(ref calls);return Task.FromResult(new OperationReceipt(true,"y",request.Rid!,"done",request.Op));})){await WaitRouterStateAsync(foreignState,"foreign-row","recovery_required");Check((await replacement.RegisterTrayAsync(new TrayRegistration(owner,7,"new-owner"),new LocalPeer(owner,7))).Ok,"new owner tray rejected");await Task.Delay(100);}Equal(0,calls);
}

static Task RootCodeStoreAsync()
{
    using var temp=new TempRoot();var path=Path.Combine(temp.Path,"root.verifier");var verifier=new RootCodeVerifier(path);for(var i=0;i<10;i++)verifier.Enroll($"code{i}",$"A{i:000}"[^4..]);Equal(10,verifier.Labels().Count);Check(verifier.Verify("A000")&&verifier.Verify("A009"),"enrolled code rejected");try{verifier.Enroll("duplicate","A009");throw new Exception("expected root_code_duplicate");}catch(InvalidOperationException ex){Equal("root_code_duplicate",ex.Message);}verifier.Enroll("code0","Z999");Check(verifier.Verify("Z999")&&!verifier.Verify("A000"),"replace failed");Check(verifier.Remove("code1")&&!verifier.Remove("code1"),"remove failed");Check(!verifier.Verify("A001"),"removed code accepted");verifier.Enroll("replacement","B001");Equal(10,verifier.Labels().Count);try{verifier.Enroll("eleventh","C001");throw new Exception("expected root_code_limit");}catch(InvalidOperationException ex){Equal("root_code_limit",ex.Message);}var bytes=File.ReadAllBytes(path);foreach(var code in new[]{"Z999","A009","B001"})Check(bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(code))>=0,"visible code missing from root store");Equal("Z999",verifier.Definitions().Single(x=>x.Label=="code0").Code);return Task.CompletedTask;
}

static async Task RootCodePolicyAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("root-policy");var allowed=temp.Dir("allowed");var denied=temp.Dir("denied");var executable=Path.GetFullPath(Environment.ProcessPath!);var owner=LocalIdentity.Current().Sid;var root=new RootCodeVerifier(Path.Combine(state,"root.verifier"));
    root.Enroll(new RootCodeDefinition("scoped","P4O7",new RootAccessPolicy("allow",[executable]),new RootAccessPolicy("allow",[allowed])));
    var calls=0;await using var broker=new AuthenticatedServiceBroker(state,owner,root,false,null,(request,_)=>{Interlocked.Increment(ref calls);return Task.FromResult(new OperationReceipt(true,"inner",request.Rid!,"done",request.Op));});
    var allowedPeer=new LocalPeer(owner,3,executable);var wrongApp=new LocalPeer(owner,3,Path.Combine(temp.Path,"other.exe"));
    var appDenied=await broker.HandleSystemAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(allowed,"app-blocked")}},"app-denied",Root:"P4O7"),wrongApp,CancellationToken.None);Equal("root_app_denied",appDenied.Err);
    var pathDenied=await broker.HandleSystemAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(denied,"blocked")}},"path-denied",Root:"P4O7"),allowedPeer,CancellationToken.None);Equal("root_path_denied",pathDenied.Err);
    var accepted=await broker.HandleSystemAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(allowed,"accepted")}},"path-allowed",Root:"P4O7"),allowedPeer,CancellationToken.None);Equal(0,accepted.ExitCode);Equal(1,calls);
    var definition=root.Definitions().Single();Equal("P4O7",definition.Code);Equal("allow",definition.Applications.Mode);Equal(executable,definition.Applications.Values.Single());Equal(Path.GetFullPath(allowed),definition.Folders.Values.Single());
    root.Enroll(new RootCodeDefinition("blocked","D3N7",new RootAccessPolicy("deny",[executable]),new RootAccessPolicy("deny",[denied])));var blocked=root.Match("D3N7")!;
    Check(!RootCodeVerifier.AllowsApplication(blocked,executable),"application denylist allowed a listed executable");Check(RootCodeVerifier.AllowsApplication(blocked,Path.GetFullPath(FakeToolExe())),"application denylist blocked an unlisted executable");
    Check(!RootCodeVerifier.AllowsFolders(blocked,new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(denied,"child")}})),"folder denylist allowed a listed subtree");Check(RootCodeVerifier.AllowsFolders(blocked,new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(allowed,"child")}})),"folder denylist blocked an unrelated root");
}

static async Task PipeClientApplicationAsync()
{
    var pipeName="pao-app-identity-"+Guid.NewGuid().ToString("N");var owner=LocalIdentity.Current().Sid;LocalPeer? captured=null;
    var server=Task.Run(async()=>{await using var pipe=LocalPipeFactory.Create(pipeName,owner);await pipe.WaitForConnectionAsync();captured=LocalIdentity.Capture(pipe);});
    await using(var client=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation))
    {
        await client.ConnectAsync(2000);
        // Capture requires the client handle to remain connected. Closing it
        // immediately after ConnectAsync races the server's impersonation.
        await server.WaitAsync(TimeSpan.FromSeconds(3));
    }
    Check(captured is not null,"client identity was not captured");Equal(owner,captured!.Sid);Equal(Path.GetFullPath(Environment.ProcessPath!),captured.ApplicationPath);
}

static async Task OwnerControlAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("control-state");var code="Q7Z9";
    using(var service=await StartServiceAsync(state,startTray:false))
    {
        var enrolled=await RunPaoAsync(["root","upsert","build-admin","--stdin"],code+"\n");Equal(0,enrolled.ExitCode);var rootStore=Path.Combine(state,"root.verifier");Check(File.ReadAllText(rootStore).Contains(code,StringComparison.Ordinal),"root code is not visible in owner store");Check(!StateContainsAscii(state,code),"root code leaked into a durable job database or log");
        var labels=await RunPaoAsync(["root","ls"]);Equal(0,labels.ExitCode);Check(Uri.UnescapeDataString(Field(labels.Stdout,"labels")).Contains("build-admin",StringComparison.Ordinal),labels.Stdout);Check(Uri.UnescapeDataString(Field(labels.Stdout,"codes")).Contains(code,StringComparison.Ordinal),"owner listing omitted visible code");
        var removed=await RunPaoAsync(["root","rm","build-admin"]);Equal(0,removed.ExitCode);Check(!new RootCodeVerifier(Path.Combine(state,"root.verifier")).Verify(code),"removed root code still verifies");
    }

    using var routed=await StartServiceAsync(state,startTray:true);
    var queueStat=await RunPaoAsync(["queue","stat"]);Check(queueStat.ExitCode==0,$"queue stat failed: {queueStat.Stdout} {queueStat.Stderr}");var pause=await RunPaoAsync(["queue","pause"]);Check(pause.ExitCode==0,$"queue pause failed: {pause.Stdout} {pause.Stderr}");var paused=await RunPaoAsync(["queue","stat"]);Check(paused.Stdout.Contains("paused=true",StringComparison.OrdinalIgnoreCase),paused.Stdout);var resume=await RunPaoAsync(["queue","resume"]);Check(resume.ExitCode==0,$"queue resume failed: {resume.Stdout} {resume.Stderr}");
    var executable=FakeToolExe();using var stream=File.OpenRead(executable);var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    var rejectedEnvironment=new RegisteredToolDefinition("bad-env",1,executable,sha,Environment:new Dictionary<string,string?>{{"SECRET_TOKEN","must-not-persist"}});var rejected=await RunPaoAsync(["tool","add","--stdin","rid=tool-env-reject"],JsonSerializer.Serialize(rejectedEnvironment,new JsonSerializerOptions(JsonSerializerDefaults.Web)));Equal(2,rejected.ExitCode);Check(!StateContainsAscii(state,"must-not-persist"),"rejected tool environment persisted");
    var workspace=temp.Dir("recipe workspace 雪 &");Equal(0,(await RunPaoAsync(["ctx","add","recipews",workspace])).ExitCode);
    var tool=new RegisteredToolDefinition("fixture",1,executable,sha,WorkingDirectoryRule:ToolWorkingDirectoryRule.Workspace,TimeoutSeconds:30,ExpectedExitCodes:[0],MaxArguments:16,Environment:new Dictionary<string,string?>{{"DOTNET_ROOT",Path.GetDirectoryName(Dotnet())}});
    var toolAdd=await RunPaoAsync(["tool","add","--stdin","rid=tool-add"],JsonSerializer.Serialize(tool,new JsonSerializerOptions(JsonSerializerDefaults.Web)));Equal(0,toolAdd.ExitCode);
    var recipe=new OneActionRecipeDefinition("echo-one",1,RecipeActionKind.Tool,"fixture",["fixed","{{value}}"],[new RecipeVariableDefinition("value")],Workspace:"recipews",TimeoutSeconds:30,ExpectedExitCodes:[0]);
    Equal(0,(await RunPaoAsync(["recipe","add","--stdin","rid=recipe-add"],JsonSerializer.Serialize(recipe,new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ExitCode);
    var variables=JsonSerializer.Serialize(new Dictionary<string,string>{{"value","snow 雪 & = ok"}});var run=await RunPaoAsync(["do","echo-one","--stdin","rid=recipe-run"],variables);Equal(0,run.ExitCode);
    var argv=JsonSerializer.Deserialize<string[]>(Uri.UnescapeDataString(Field(run.Stdout,"out")))!;Equal(new[]{"fixed","snow 雪 & = ok"},argv);
    var guarded=await RunPaoAsync(["tool","rm","fixture","rid=tool-guard"]);Equal(3,guarded.ExitCode);Equal("precondition",Field(guarded.Stdout,"err"));
    Equal(0,(await RunPaoAsync(["recipe","rm","echo-one","rid=recipe-rm"])).ExitCode);Equal(0,(await RunPaoAsync(["tool","rm","fixture","rid=tool-rm"])).ExitCode);
}

static async Task DotnetRegistrationEnvironmentAsync()
{
    using var temp=new TempRoot();
    var state=temp.Dir("dotnet-registration");
    await using var engine=new AgentOpsEngine(state);
    var executable=Dotnet();
    using var stream=File.OpenRead(executable);
    var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    var definition=new RegisteredToolDefinition(
        "dotnet",1,executable,sha,
        WorkingDirectoryRule:ToolWorkingDirectoryRule.Workspace,
        TimeoutSeconds:3600,
        ExpectedExitCodes:[0],
        MaxArguments:256,
        VersionProbe:new ToolProbeDefinition(["--version"],[0],"10.0."));
    var request=new OperationRequest(
        "tool",
        new Dictionary<string,string>{{"op","add"}},
        Body:JsonSerializer.Serialize(definition,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var receipt=await engine.ExecuteAsync(request);
    Equal(0,receipt.ExitCode);
    var registered=new ToolRecipeOperations(state).Tools.Get("dotnet");
    var userProfile=Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    Equal(userProfile,registered.Environment!["DOTNET_CLI_HOME"]);
    Equal(Path.Combine(userProfile,".nuget","packages"),registered.Environment["NUGET_PACKAGES"]);
    Equal(userProfile,registered.Environment["USERPROFILE"]);
    Equal(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),registered.Environment["APPDATA"]);
    Equal(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),registered.Environment["LOCALAPPDATA"]);
    Equal(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),registered.Environment["ProgramFiles"]);
    Equal(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),registered.Environment["ProgramFiles(x86)"]);
    Equal(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),registered.Environment["ProgramData"]);
    Equal(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd(Path.DirectorySeparatorChar),registered.Environment["SystemDrive"]);
    Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("ComSpec")!),registered.Environment["ComSpec"]);
    var pwsh=await PowerShell7ToolBootstrap.EnsureAsync(state);
    Equal(userProfile,pwsh.Environment!["USERPROFILE"]);
    Equal(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),pwsh.Environment["ProgramFiles(x86)"]);
}

static async Task RegisteredSevenZipAsync()
{
    var sevenZip=@"C:\Program Files\7-Zip\7z.exe";if(!File.Exists(sevenZip))return;
    using var temp=new TempRoot();var state=temp.Dir("seven-public-state");using var service=await StartServiceAsync(state,startTray:true);using var stream=File.OpenRead(sevenZip);var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    var definition=new RegisteredToolDefinition("7z",1,sevenZip,sha,TimeoutSeconds:60,ExpectedExitCodes:[0],MaxArguments:64,VersionProbe:new ToolProbeDefinition(["i"],[0],"7-Zip"));
    Equal(0,(await RunPaoAsync(["tool","add","--stdin","rid=seven-register"],JsonSerializer.Serialize(definition,new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ExitCode);
    var source=temp.Dir("archive source 雪 &");await File.WriteAllTextAsync(Path.Combine(source,"a & 雪.txt"),"seven public");var archive=Path.Combine(temp.Path,"bundle & 雪.zip");Equal(0,(await RunPaoAsync(["zip","create",source,archive,"rid=seven-zip"])).ExitCode);
    var listed=await RunPaoAsync(["7z","list",archive,"rid=seven-list"]);Equal(0,listed.ExitCode);Equal(1L,LongField(listed.Stdout,"count"));var tested=await RunPaoAsync(["7z","test",archive,"rid=seven-test"]);Equal(0,tested.ExitCode);
    var destination=Path.Combine(temp.Path,"seven out 雪 &");var extracted=await RunPaoAsync(["7z","extract",archive,destination,"rid=seven-extract"]);Equal(0,extracted.ExitCode);Equal("seven public",await File.ReadAllTextAsync(Path.Combine(destination,"a & 雪.txt")));
}

static async Task ServiceWaitsForTrayAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");using var service=await StartServiceAsync(state,startTray:false);var dst=Path.Combine(temp.Path,"must-wait");var receipt=await RunPaoAsync(["mkdir",dst,"rid=waiting-owner"]);Equal(0,receipt.ExitCode);Equal("waiting_user",Field(receipt.Stdout,"state"));Check(!Directory.Exists(dst),"rootless job executed without owner tray");var row=ReadRouterIdentity(state,"waiting-owner");var current=LocalIdentity.Current();Equal(current.Sid,row.Sid);Equal(0,row.Session);Equal("waiting_user",row.State);
}

static async Task AuthenticatedTrayAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");using var service=await StartServiceAsync(state,startTray:false);var dst=Path.Combine(temp.Path,"claimed");var waiting=await RunPaoAsync(["mkdir",dst,"rid=tray-claim"]);Equal("waiting_user",Field(waiting.Stdout,"state"));using var tray=await StartTrayAsync(state);var deadline=Stopwatch.StartNew();while(!Directory.Exists(dst)&&deadline.Elapsed<TimeSpan.FromSeconds(8))await Task.Delay(50);Check(Directory.Exists(dst),"authenticated tray did not execute waiting job");var row=ReadRouterIdentity(state,"tray-claim");var current=LocalIdentity.Current();Equal(current.Sid,row.Sid);Equal(0,row.Session);Equal("done",row.State);var status=await RunPaoAsync(["job","stat",Field(waiting.Stdout,"jid")]);Equal("done",Field(status.Stdout,"state"));Equal(0L,EngineJobCount(Path.Combine(state,"tray-default")));
}

static async Task OwnerSessionChangeAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var owner=LocalIdentity.Current().Sid;var dst=Path.Combine(temp.Path,"resumed");string jid;
    var rootPath=Path.Combine(state,"root.verifier");
    await using(var first=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(rootPath)))
    {
        var queued=await first.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",dst}},"session-change"),new LocalPeer(owner,3),CancellationToken.None);
        Equal("waiting_user",queued.State);jid=queued.Jid;
        var zero=await first.HandleUserAsync(new OperationRequest("diag",new Dictionary<string,string>(),"session-zero"),new LocalPeer(owner,0),CancellationToken.None);
        Equal("auth",zero.Err);
    }
    var executions=0;
    await using(var restarted=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(rootPath),false,(peer,_,request,_)=>
    {
        Equal(7,peer.Session);Interlocked.Increment(ref executions);Directory.CreateDirectory(request.Arg["dst"]);return Task.FromResult(new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid!,"done",request.Op));
    }))
    {
        var other=await restarted.HandleUserAsync(new OperationRequest("diag",new Dictionary<string,string>(),"wrong-owner"),new LocalPeer("S-1-5-21-1064206094-724938797-970556320-424242",7),CancellationToken.None);
        Equal("auth",other.Err);
        var resumedStatus=await restarted.HandleUserAsync(new OperationRequest("job",new Dictionary<string,string>{{"op","stat"},{"jid",jid}},"session-seven-request"),new LocalPeer(owner,7),CancellationToken.None);
        Equal("waiting_user",resumedStatus.State);
        var mismatch=await restarted.RegisterTrayAsync(new TrayRegistration(owner,3,"fixture-tray"),new LocalPeer(owner,7));
        Check(!mismatch.Ok,"mismatched declared tray session accepted");
        var accepted=await restarted.RegisterTrayAsync(new TrayRegistration(owner,7,"fixture-tray"),new LocalPeer(owner,7));
        Check(accepted.Ok,"later owner session rejected");
        await WaitRouterStateAsync(state,"session-change","done");
        await restarted.RegisterTrayAsync(new TrayRegistration(owner,7,"fixture-tray"),new LocalPeer(owner,7));
        await Task.Delay(150);
    }
    Equal(1,executions);Check(Directory.Exists(dst),"waiting owner work did not resume");
}

static async Task InstalledConfigProcessAsync()
{
    using var temp=new TempRoot();var owner=LocalIdentity.Current().Sid;
    async Task CheckConfigAsync(string json,bool valid)
    {
        var root=temp.Dir("cfg-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"service.v1.json");await File.WriteAllTextAsync(path,json);Equal(0,(await RunProcessAsync(Dotnet(),[ServiceDll(),"--prepare-config-fixture",root])).ExitCode);var result=await RunProcessAsync(Dotnet(),[ServiceDll(),"--validate-config-fixture",path,root]);Equal(valid?0:4,result.ExitCode);if(!valid)Check(result.Stderr.Contains("err=service_config_invalid",StringComparison.Ordinal),result.Stderr);
    }
    await CheckConfigAsync(JsonSerializer.Serialize(new{contract="pegasus-agentops-service",version=1,ownerSid=owner}),true);
    await CheckConfigAsync("null",false);
    await CheckConfigAsync(JsonSerializer.Serialize(new{ownerSid=owner}),false);
    await CheckConfigAsync(JsonSerializer.Serialize(new{contract="pegasus-agentops-service",version=2,ownerSid=owner}),false);
    await CheckConfigAsync(JsonSerializer.Serialize(new{contract="pegasus-agentops-service",version=1,ownerSid="not-a-sid"}),false);
    await CheckConfigAsync(JsonSerializer.Serialize(new{contract="pegasus-agentops-service",version=1,ownerSid=owner,ownerSession=3}),false);
    var external=temp.Dir("external-config");await File.WriteAllTextAsync(Path.Combine(external,"service.v1.json"),JsonSerializer.Serialize(new{contract="pegasus-agentops-service",version=1,ownerSid=owner}));var linked=Path.Combine(temp.Path,"linked-config");await RunProcessAsync(Pwsh(),["-NoProfile","-NonInteractive","-File",JunctionScript(),linked,external]);var reparse=await RunProcessAsync(Dotnet(),[ServiceDll(),"--validate-config-fixture",Path.Combine(linked,"service.v1.json"),linked]);Equal(4,reparse.ExitCode);Check(reparse.Stderr.Contains("err=service_config_invalid",StringComparison.Ordinal),reparse.Stderr);Directory.Delete(linked);
    var unsafeRoot=temp.Dir("unsafe-acl");var unsafeConfig=Path.Combine(unsafeRoot,"service.v1.json");await File.WriteAllTextAsync(unsafeConfig,JsonSerializer.Serialize(new{contract="pegasus-agentops-service",version=1,ownerSid=owner}));Equal(0,(await RunProcessAsync(Dotnet(),[ServiceDll(),"--prepare-config-fixture",unsafeRoot])).ExitCode);var acl=new FileInfo(unsafeConfig).GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),FileSystemRights.Read,AccessControlType.Allow));new FileInfo(unsafeConfig).SetAccessControl(acl);var unsafeResult=await RunProcessAsync(Dotnet(),[ServiceDll(),"--validate-config-fixture",unsafeConfig,unsafeRoot]);Equal(4,unsafeResult.ExitCode);Check(unsafeResult.Stderr.Contains("err=service_state_acl_invalid",StringComparison.Ordinal)&&unsafeResult.Stderr.Contains("entry=service.v1.json",StringComparison.Ordinal),unsafeResult.Stderr);
}

static async Task PipeSubmissionAmbiguityAsync()
{
    var pipeName="pao-ambiguous-"+Guid.NewGuid().ToString("N");var server=Task.Run(async()=>{await using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);await pipe.WaitForConnectionAsync();_ = await BinaryProtocol.ReadAsync<OperationRequest>(pipe);});
    await Task.Delay(50);await using var dispatcher=new NamedPipeDispatcher(pipeName);var receipt=await dispatcher.DispatchAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",new string('x',32_000)}},"ambiguous-write"));await server;Equal("recovery_required",receipt.Err);Check(receipt.Partial,"ambiguous submission not marked partial");
}

static async Task RoutingBoundaryAsync()
{
    var user = new RecordingDispatcher(request => Success(request, "user-host"));
    var service = new RecordingDispatcher(request => Success(request, "service-user"));
    var system = new RecordingDispatcher(request => Success(request, "system"));
    await using var dispatcher = new RoutingDispatcher(user, service, system);

    var ordinary = await dispatcher.DispatchAsync(new OperationRequest("stat", new Dictionary<string,string>{{"src","C:\\fixture"}}, "route-user"));
    Equal("user-host", ordinary.Data!["route"]?.ToString());

    var rootAdmin = await dispatcher.DispatchAsync(new OperationRequest("root", new Dictionary<string,string>{{"op","ls"}}, "route-root-admin"));
    Equal("service-user", rootAdmin.Data!["route"]?.ToString());

    var elevated = await dispatcher.DispatchAsync(new OperationRequest("stat", new Dictionary<string,string>{{"src","C:\\fixture"}}, "route-system", Root:"AB12"));
    Equal("system", elevated.Data!["route"]?.ToString());

    Equal(1, user.Count);
    Equal(1, service.Count);
    Equal(1, system.Count);

    static OperationReceipt Success(OperationRequest request,string route) =>
        new(true,Guid.NewGuid().ToString("N"),request.Rid!,"done",OperationNames.Public(request),Data:new Dictionary<string,object?>{{"route",route}});
}

static async Task NestedMutationGuardAsync()
{
    var previous=Environment.GetEnvironmentVariable(AgentOpsExecutionContext.MutationDepthEnvironmentVariable);
    try
    {
        Environment.SetEnvironmentVariable(AgentOpsExecutionContext.MutationDepthEnvironmentVariable,"1");
        var user=new RecordingDispatcher(request=>new(true,"user-jid",request.Rid!,"done",OperationNames.Public(request)));
        var service=new RecordingDispatcher(request=>throw new InvalidOperationException("nested mutation reached service routing"));
        var system=new RecordingDispatcher(request=>throw new InvalidOperationException("nested mutation reached system routing"));
        await using var dispatcher=new RoutingDispatcher(user,service,system);

        var read=await dispatcher.DispatchAsync(new OperationRequest("stat",new Dictionary<string,string>{{"src","C:\\fixture"}},"nested-read"));
        Check(read.Ok,"read-only nested operation was rejected");
        Equal(1,user.Count);

        var mutation=await dispatcher.DispatchAsync(new OperationRequest("dotnet",new Dictionary<string,string>{{"op","build"},{"src","C:\\fixture.csproj"}},"nested-build"));
        Equal("reentrant_mutation",mutation.Err);
        Equal("nested-build",mutation.Rid);
        Equal(3,mutation.ExitCode);
        Check(mutation.Remedy?.Contains("directly outside PAO",StringComparison.Ordinal)==true,"nested mutation remedy is not actionable");
        Equal(1,user.Count);
        Equal(0,service.Count);
        Equal(0,system.Count);
    }
    finally
    {
        Environment.SetEnvironmentVariable(AgentOpsExecutionContext.MutationDepthEnvironmentVariable,previous);
    }
}

static async Task RoutingFallbackAsync()
{
    var service = new RecordingDispatcher(request => throw new InvalidOperationException("ordinary request entered the privileged service route"));
    var system = new RecordingDispatcher(request => throw new InvalidOperationException("system route was not expected"));

    var attempts = 0;
    await using (var attachOnly = new RoutingDispatcher(
        new RecordingDispatcher(request =>
        {
            Interlocked.Increment(ref attempts);
            return new OperationReceipt(false,"direct-jid",request.Rid!,"failed",OperationNames.Public(request),"svc_down");
        }), service, system))
    {
        var receipt = await attachOnly.DispatchAsync(new OperationRequest("diag",new Dictionary<string,string>(),"host-off-rid"));
        Equal("user_host_unavailable",receipt.Err);
        Check(!receipt.Partial,"absent host was marked submitted");
    }
    Equal(1,attempts);
    Equal(0,service.Count);

    await using (var unavailable = new RoutingDispatcher(
        new RecordingDispatcher(request => new OperationReceipt(false,"direct-jid",request.Rid!,"failed",OperationNames.Public(request),"svc_down")),
        service,
        system))
    {
        var receipt = await unavailable.DispatchAsync(new OperationRequest("diag",new Dictionary<string,string>(),"host-absent-rid"));
        Equal("user_host_unavailable",receipt.Err);
    }
    Equal(0,service.Count);

    var mustNotRetry = new RecordingDispatcher(request =>
        new OperationReceipt(false,"direct-jid",request.Rid!,"attention",OperationNames.Public(request),"recovery_required",Partial:true));
    var unusedService = new RecordingDispatcher(request => throw new InvalidOperationException("ambiguous direct submission was retried"));
    await using (var noReplay = new RoutingDispatcher(mustNotRetry,unusedService,system))
    {
        var receipt = await noReplay.DispatchAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst","C:\\fixture"}},"ambiguous-direct"));
        Equal("recovery_required",receipt.Err);
        Check(receipt.Partial,"ambiguous direct submission lost its partial marker");
    }
    Equal(0,unusedService.Count);
}

static async Task DirectUserHostWithoutServiceAsync()
{
    using var temp = new TempRoot();
    var owner = LocalIdentity.Current();
    var pipeName = "pao-direct-user-" + Guid.NewGuid().ToString("N");
    var unavailableServicePipe = "pao-no-service-" + Guid.NewGuid().ToString("N");
    using var stop = new CancellationTokenSource();
    await using var engine = UserHostLifecycleTests.CreateOpenEngine(temp.Dir("user-state"));
    var host = AuthenticatedPipeHost.RunOperationsAsync(
        pipeName,
        owner.Sid,
        (request,peer,ct) => peer.SameIdentity(owner)
            ? engine.ExecuteAsync(request,ct)
            : Task.FromResult(new OperationReceipt(false,"auth-jid",request.Rid!,"failed",OperationNames.Public(request),"auth")),
        stop.Token);
    await Task.Delay(50);

    await using var dispatcher = new RoutingDispatcher(
        new NamedPipeDispatcher(pipeName,TimeSpan.FromSeconds(2),owner),
        new NamedPipeDispatcher(unavailableServicePipe,TimeSpan.FromMilliseconds(50),owner),
        new RecordingDispatcher(request => throw new InvalidOperationException("system route was not expected")));
    var diag = await dispatcher.DispatchAsync(new OperationRequest("diag",new Dictionary<string,string>(),"direct-no-service-diag"));
    Check(diag.Ok,diag.Err??"direct host diag failed");
    var destination = Path.Combine(temp.Path,"direct-user-operation");
    var mkdir = await dispatcher.DispatchAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",destination}},"direct-no-service-mkdir"));
    Check(mkdir.Ok,mkdir.Err??"direct user mutation failed");
    Check(Directory.Exists(destination),"ordinary user mutation remained blocked on the absent service");

    stop.Cancel();
    try { await host; }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}

static async Task RootWithoutTrayAsync()
{
    using var temp = new TempRoot();
    var owner = LocalIdentity.Current();
    var verifier = new RootCodeVerifier(Path.Combine(temp.Path,"root.verifier"));
    verifier.Enroll("AB12");
    var systemCalls = 0;
    await using var broker = new AuthenticatedServiceBroker(
        temp.Dir("service-state"),
        owner.Sid,
        verifier,
        false,
        null,
        (request,_) =>
        {
            Interlocked.Increment(ref systemCalls);
            return Task.FromResult(new OperationReceipt(true,"system-jid",request.Rid!,"done",OperationNames.Public(request)));
        });

    var receipt = await broker.HandleSystemAsync(
        new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"root-target")}},"root-no-tray",Root:"AB12"),
        owner,
        CancellationToken.None);
    Check(receipt.Ok,receipt.Err??"root request failed without a tray");
    Equal(1,systemCalls);
}

static async Task BrokerSequenceGateAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var owner=LocalIdentity.Current().Sid;var order=new List<string>();var gate=new object();
    await using var broker=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,async(_,_,request,_)=>{if(request.Rid=="older")await Task.Delay(150);lock(gate)order.Add(request.Rid!);return new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid!,"done",request.Op);});
    var older=await broker.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"older")}},"older"),new LocalPeer(owner,3),CancellationToken.None);Equal("waiting_user",older.State);
    Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,7,"sequence-tray"),new LocalPeer(owner,7))).Ok,"tray registration failed");
    var submits=Enumerable.Range(0,4).Select(i=>broker.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"new"+i)}},"new"+i),new LocalPeer(owner,7),CancellationToken.None)).ToArray();await Task.WhenAll(submits);await WaitRouterStateAsync(state,"new3","done");
    Equal("older",order[0]);Equal(5,order.Count);using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"agentops-router.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT rid FROM routed_jobs ORDER BY seq";using var reader=command.ExecuteReader();var persisted=new List<string>();while(reader.Read())persisted.Add(reader.GetString(0));Equal(persisted.ToArray(),order.ToArray());
}

static async Task TrayReconnectRaceAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var owner=LocalIdentity.Current().Sid;var oldEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var releaseOld=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var executions=0;
    await using var broker=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,async(peer,_,request,_)=>{if(peer.Session==3){oldEntered.TrySetResult();await releaseOld.Task;return new OperationReceipt(false,"x",request.Rid!,"failed",request.Op,"svc_down");}Interlocked.Increment(ref executions);return new OperationReceipt(true,"y",request.Rid!,"done",request.Op);});
    Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"old"),new LocalPeer(owner,3))).Ok,"old tray rejected");var pending=broker.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"race")}},"reconnect"),new LocalPeer(owner,3),CancellationToken.None);await oldEntered.Task;Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,7,"new"),new LocalPeer(owner,7))).Ok,"new tray rejected");releaseOld.TrySetResult();await pending;await WaitRouterStateAsync(state,"reconnect","done");Equal(1,executions);
}

static async Task SystemDiagNoReplayAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var system=Directory.CreateDirectory(Path.Combine(state,"system")).FullName;var marker=Path.Combine(temp.Path,"must-not-execute");await File.WriteAllTextAsync(Path.Combine(system,"agentops.db"),"preseeded queued run "+marker);var owner=LocalIdentity.Current().Sid;await using var broker=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")));await Task.Delay(200);Check(!File.Exists(marker),"System replay state executed");Check(File.Exists(Path.Combine(system,"agentops.db")),"System state was opened or replaced");
}

static async Task ReleaseIdentityOverridesAsync()
{
    foreach(var project in new[]{Path.Combine(Root(),"src","Pegasus.AgentOps.Service","Pegasus.AgentOps.Service.csproj"),Path.Combine(Root(),"src","Pegasus.AgentOps.Tray","Pegasus.AgentOps.Tray.csproj")})
    {
        var build=await RunProcessAsync(Dotnet(),["build",project,"-c","Release","--no-restore"]);Equal(0,build.ExitCode);
    }
    var forbidden=new[]{"PAO_OWNER_SID","PAO_TEST_CRASH_AFTER_TRAY","PAO_TRAY_DECLARED_SID","PAO_TRAY_DECLARED_SESSION","PAO_SERVICE_SID","PAO_SERVICE_SESSION","--console-fixture","--fixture-identity"};
    foreach(var dll in new[]{Path.Combine(Root(),"src","Pegasus.AgentOps.Service","bin","Release","net10.0-windows","win-x64","PegasusCortex.ExecutionService.dll"),Path.Combine(Root(),"src","Pegasus.AgentOps.Tray","bin","Release","net10.0-windows","win-x64","PegasusCortex.Host.dll")})
    {
        var binary=Encoding.UTF8.GetString(await File.ReadAllBytesAsync(dll));foreach(var value in forbidden)Check(!binary.Contains(value,StringComparison.Ordinal),$"Release binary contains {value}");
    }
    foreach(var source in new[]{Path.Combine(Root(),"src","Pegasus.AgentOps.Service","Program.cs"),Path.Combine(Root(),"src","Pegasus.AgentOps.Tray","Program.cs")})
    {
        var releaseSource=StripDebugBlocks(await File.ReadAllTextAsync(source));foreach(var value in forbidden)Check(!releaseSource.Contains(value,StringComparison.Ordinal),$"Release source contains {value}");
    }
}

static string StripDebugBlocks(string source)
{
    var output=new StringBuilder();var debugDepth=0;foreach(var line in source.Split('\n')){var trimmed=line.Trim();if(trimmed=="#if DEBUG"){debugDepth++;continue;}if(trimmed=="#endif"&&debugDepth>0){debugDepth--;continue;}if(debugDepth==0)output.AppendLine(line);}return output.ToString();
}

static async Task WrongTrayIdentityAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");using var service=await StartServiceAsync(state,startTray:false);var wrongSid=await RunTrayToExitAsync(state,new Dictionary<string,string>{{"PAO_TRAY_DECLARED_SID","S-1-5-18"}});Equal(4,wrongSid.ExitCode);Check(wrongSid.Stderr.Contains("err=auth",StringComparison.Ordinal),wrongSid.Stderr);var wrongSession=await RunTrayToExitAsync(state,new Dictionary<string,string>{{"PAO_TRAY_DECLARED_SESSION",(LocalIdentity.Current().Session+1).ToString()}});Equal(4,wrongSession.ExitCode);var dst=Path.Combine(temp.Path,"still-waiting");var waiting=await RunPaoAsync(["mkdir",dst,"rid=wrong-tray"]);Equal("waiting_user",Field(waiting.Stdout,"state"));Check(!Directory.Exists(dst),"rejected tray executed work");
}

static async Task ServiceAliasFreezeAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var one=temp.Dir("one");var two=temp.Dir("two");var src=Directory.CreateDirectory(Path.Combine(one,"src")).FullName;File.WriteAllText(Path.Combine(src,"new.txt"),"new");var dst1=Directory.CreateDirectory(Path.Combine(one,"dst")).FullName;var dst2=Directory.CreateDirectory(Path.Combine(two,"dst")).FullName;var marker=Path.Combine(dst2,"marker.txt");File.WriteAllText(marker,"safe");using var service=await StartServiceAsync(state,startTray:false);Equal(0,(await RunPaoAsync(["ctx","add","target",one])).ExitCode);var waiting=await RunPaoAsync(["cp","src","dst","pol=clean","ws=target","rid=service-alias"]);Equal("waiting_user",Field(waiting.Stdout,"state"));var rebind=RunPaoAsync(["ctx","add","target",two]);using var tray=await StartTrayAsync(state,"alias");Equal(0,(await rebind).ExitCode);await WaitRouterStateAsync(state,"service-alias","done");Equal("new",File.ReadAllText(Path.Combine(dst1,"new.txt")));Equal("safe",File.ReadAllText(marker));
}

static async Task RepeatedTrayRegistrationAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var count=Path.Combine(temp.Path,"count.txt");var argsPath=Path.Combine(temp.Path,"args.json");using var service=await StartServiceAsync(state,startTray:false);var waiting=await RunPaoAsync(["dotnet","build","demo.csproj","rid=one-execution"]);Equal("waiting_user",Field(waiting.Stdout,"state"));using var first=await StartTrayAsync(state,"first",count,argsPath);using var second=await StartTrayAsync(state,"second",count,argsPath);await WaitRouterStateAsync(state,"one-execution","done");Equal(1,File.ReadAllLines(count).Length);
}

static async Task TrayCommitCrashAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");var count=Path.Combine(temp.Path,"crash-count.txt");var argsPath=Path.Combine(temp.Path,"args.json");using(var crashing=await StartServiceAsync(state,startTray:false,crashAfterTray:true)){var waiting=await RunPaoAsync(["dotnet","build","demo.csproj","rid=crash-job"]);Equal("waiting_user",Field(waiting.Stdout,"state"));using var tray=await StartTrayAsync(state,"before-crash",count,argsPath);await crashing.WaitForExitAsync(TimeSpan.FromSeconds(10));Equal(91,crashing.ExitCode);}Equal(1,File.ReadAllLines(count).Length);using var restarted=await StartServiceAsync(state,startTray:false);using var replacement=await StartTrayAsync(state,"after-crash",count,argsPath);await WaitRouterStateAsync(state,"crash-job","recovery_required");await Task.Delay(300);Equal(1,File.ReadAllLines(count).Length);
}

static async Task SquattedSystemPipeAsync()
{
    await using var fake=new NamedPipeServerStream(NamedPipeDispatcher.SystemPipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);var connected=fake.WaitForConnectionAsync();var resultTask=RunPaoWithoutConsoleOverrideAsync(["root=AB12","diag","rid=squatted-root"]);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await connected.WaitAsync(timeout.Token);var buffer=new byte[1];var read=await fake.ReadAsync(buffer,timeout.Token);var result=await resultTask;Equal(0,read);Equal(4,result.ExitCode);Check(result.Stdout.Contains("err=auth",StringComparison.Ordinal),result.Stdout);
}

static Task PipeAclAsync()
{
    var clientSid="S-1-5-21-1064206094-724938797-970556320-424242";using var pipe=LocalPipeFactory.Create("pao-acl-test-"+Guid.NewGuid().ToString("N"),clientSid);var rules=pipe.GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();PipeAccessRule Rule(string sid,AccessControlType type)=>rules.Single(x=>x.IdentityReference.Value==sid&&x.AccessControlType==type);var server=Rule(LocalIdentity.Current().Sid,AccessControlType.Allow);Check((server.PipeAccessRights&PipeAccessRights.FullControl)==PipeAccessRights.FullControl,"server lacks full control");var client=Rule(clientSid,AccessControlType.Allow);Check((client.PipeAccessRights&PipeAccessRights.ReadWrite)==PipeAccessRights.ReadWrite&& (client.PipeAccessRights&PipeAccessRights.Synchronize)!=0,"client lacks rw/sync");Check((client.PipeAccessRights&PipeAccessRights.CreateNewInstance)==0,"client can create pipe instance");_ = Rule("S-1-5-2",AccessControlType.Deny);_ = Rule("S-1-5-7",AccessControlType.Deny);return Task.CompletedTask;
}

static async Task RootSystemDispatchAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("state");new RootCodeVerifier(Path.Combine(state,"root.verifier")).Enroll("AB12");using var service=await StartServiceAsync(state,startTray:false);var invalid=await RunPaoAsync(["root=ZZ99","diag","rid=bad-root"]);Equal(4,invalid.ExitCode);Equal("auth",Field(invalid.Stdout,"err"));Equal(0L,SystemJobCount(state));var valid=await RunPaoAsync(["root=AB12","diag","rid=good-root"]);Check(valid.ExitCode==0,$"valid root diag exit={valid.ExitCode} out={valid.Stdout} err={valid.Stderr}");Equal(0L,SystemJobCount(state));var systemDirectory=Path.Combine(temp.Path,"system-created");var made=await RunPaoAsync(["root=AB12","mkdir",systemDirectory,"rid=system-mkdir"]);Check(made.ExitCode==0,$"valid root mkdir exit={made.ExitCode} out={made.Stdout} err={made.Stderr}");Check(Directory.Exists(systemDirectory),"authorized System mkdir did not execute");Equal(0L,EngineJobCount(Path.Combine(state,"system-direct")));
    var retrySource=Path.Combine(temp.Path,"retry-source.txt");var retryDestination=Path.Combine(temp.Path,"retry-destination.txt");var failed=await RunPaoAsync(["root=AB12","cp",retrySource,retryDestination,"rid=system-failed-copy"]);Equal(3,failed.ExitCode);var failedJid=Field(failed.Stdout,"jid");var rootlessRetry=await RunPaoAsync(["job","retry",failedJid,"rid=system-retry-without-code"]);Equal(3,rootlessRetry.ExitCode);File.WriteAllText(retrySource,"retried");var retried=await RunPaoAsync(["root=AB12","job","retry",failedJid,"rid=system-retry"]);Equal(0,retried.ExitCode);var waited=await RunPaoAsync(["job","wait",Field(retried.Stdout,"jid")]);Equal(0,waited.ExitCode);Equal("retried",File.ReadAllText(retryDestination));
    var mcpDirectory=Path.Combine(temp.Path,"mcp system 雪 &");var mcp="{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"root-test\",\"version\":\"1\"}}}\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"pao\",\"arguments\":{\"op\":\"diag\",\"root\":\"AB12\",\"rid\":\"mcp-root\"}}}\n{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"pao\",\"arguments\":{\"op\":\"mkdir\",\"dst\":"+JsonSerializer.Serialize(mcpDirectory)+",\"root\":\"AB12\",\"rid\":\"mcp-system-mkdir\"}}}\n";var mcpResult=await RunPaoAsync(["mcp"],mcp);Equal(0,mcpResult.ExitCode);Check(mcpResult.Stdout.Contains("mcp-root",StringComparison.Ordinal),mcpResult.Stdout);Check(mcpResult.Stdout.Contains("mcp-system-mkdir",StringComparison.Ordinal),mcpResult.Stdout);Check(Directory.Exists(mcpDirectory),"MCP System mkdir did not execute");var marker=Path.Combine(temp.Path,"must-not-run");var denied=await RunPaoAsync(["root=AB12","run",marker,"rid=system-run"]);Equal(2,denied.ExitCode);Check(!File.Exists(marker),"arbitrary System action executed");Equal(0L,SystemJobCount(state));Check(!StateContainsAscii(state,"AB12"),"root code persisted");
}

static async Task UserSystemFifoAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("mixed-fifo");var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,3);var root=new RootCodeVerifier(Path.Combine(state,"root.verifier"));root.Enroll("mixed","A1B2");var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var order=new List<string>();var active=0;var maximum=0;
    async Task<OperationReceipt> Execute(string context,OperationRequest request){var now=Interlocked.Increment(ref active);maximum=Math.Max(maximum,now);lock(order)order.Add(context+":"+request.Rid);if(request.Rid=="user-first"){entered.TrySetResult();await release.Task;}await Task.Delay(25);Interlocked.Decrement(ref active);return new(true,"inner",request.Rid!,"done",request.Op);}
    await using var broker=new AuthenticatedServiceBroker(state,owner,root,false,(_,_,request,_)=>Execute("user",request),(request,_)=>Execute("system",request));Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"mixed-tray"),peer)).Ok,"mixed tray rejected");
    var first=broker.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"first")}},"user-first"),peer,CancellationToken.None);await entered.Task;var middle=broker.HandleSystemAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"system")}},"system-middle",Root:"A1B2"),peer,CancellationToken.None);var last=broker.HandleUserAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(temp.Path,"last")}},"user-last"),peer,CancellationToken.None);release.TrySetResult();await Task.WhenAll(first,middle,last);Equal(1,maximum);Equal(new[]{"user:user-first","system:system-middle","user:user-last"},order.ToArray());Check(!StateContainsAscii(state,"A1B2"),"mixed FIFO persisted root code");
}

static async Task TwoClientFifoAsync()
{
    using var temp = new TempRoot(); var state = temp.Dir("state"); using var service = await StartServiceAsync(state);
    var first = RunPaoAsync(["pwsh", DelayScript(), "rid=fifo-first", "--", "2"]);
    await WaitForRidAsync(state, "fifo-first"); var watch = Stopwatch.StartNew();
    var second = RunPaoAsync(["mkdir", Path.Combine(temp.Path, "after"), "rid=fifo-second"]);
    var firstResult = await first; var firstDone = watch.Elapsed;
    var secondResult = await second; var secondDone = watch.Elapsed;
    Equal(0, firstResult.ExitCode); Equal(0, secondResult.ExitCode); Check(secondDone >= firstDone, "FIFO completion order"); Check(secondDone >= TimeSpan.FromSeconds(1), "second mutation bypassed long first job");
    var firstSeq = LongField(firstResult.Stdout, "seq"); var secondSeq = LongField(secondResult.Stdout, "seq"); Check(secondSeq > firstSeq, $"FIFO sequence order first={firstResult.Stdout} second={secondResult.Stdout}");
}

static async Task WorkerSurvivalAsync()
{
    using var temp=new TempRoot();await using var engine=new AgentOpsEngine(temp.Dir("state"));var failed=await engine.ExecuteAsync(new OperationRequest("run",new Dictionary<string,string>{{"cfg","missing-tool"}},"missing-tool"));Equal("not_found",failed.Err);var made=Path.Combine(temp.Path,"made");var next=await engine.ExecuteAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",made}},"after-failure"));Check(next.Ok&&Directory.Exists(made),"worker did not survive");
}

static async Task ProcStopAsync()
{
    using var temp=new TempRoot();var pwsh=Pwsh();var start=new ProcessStartInfo(pwsh){UseShellExecute=false,CreateNoWindow=true};SetExplicitEnvironment(start,MinimumToolEnvironment(pwsh));start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-File");start.ArgumentList.Add(DelayScript());start.ArgumentList.Add("10");using var target=Process.Start(start)!;var identity=target.StartTime.ToUniversalTime().ToString("O");
    var state=temp.Dir("state");await RegisterToolAsync(state,"pwsh",pwsh);await using var engine=new AgentOpsEngine(state);var wrong=await engine.ExecuteAsync(new OperationRequest("proc",new Dictionary<string,string>{{"op","stop"},{"pid",target.Id.ToString()},{"start","wrong"}},"wrong-stop"));Equal("invalid",wrong.Err);Check(!target.HasExited,"wrong identity stopped process");
    var delay=engine.ExecuteAsync(new OperationRequest("pwsh",new Dictionary<string,string>{{"src",DelayScript()},{"arg0","2"}},"before-stop"));await Task.Delay(100);var stop=engine.ExecuteAsync(new OperationRequest("proc",new Dictionary<string,string>{{"op","stop"},{"pid",target.Id.ToString()},{"start",identity}},"exact-stop"));var first=await delay;var stopped=await stop;Check(first.Ok&&stopped.Ok,$"stop queue failed first={first.Err} stop={stopped.Err}");await target.WaitForExitAsync();Check(Convert.ToInt64(stopped.Data!["seq"])>Convert.ToInt64(first.Data!["seq"]),"stop bypassed FIFO");
}

static async Task CliFallbackAsync()
{
    using var temp = new TempRoot(); using var service = await StartServiceAsync(temp.Dir("state")); var result = await RunPaoAsync(["diag", "rid=cli-fallback"]);
    Equal(0, result.ExitCode); Check(result.Stdout.StartsWith("ok=1 jid=", StringComparison.Ordinal), result.Stdout); Equal(string.Empty, result.Stderr.Trim());
}

static async Task McpTranscriptAsync()
{
    using var temp = new TempRoot();
    var requests = new[]
    {
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\",\"title\":\"Codex-compatible client\"}}}",
        "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"pao\",\"arguments\":{\"op\":\"diag\",\"rid\":\"mcp-rid\"}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"pao_diag\",\"arguments\":{}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\",\"params\":{}}",
        "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"pao\",\"arguments\":{\"op\":\"stat\",\"dst\":\"not-valid-for-stat\"}}}"
    };
    using var service = await StartServiceAsync(temp.Dir("state"));
    var start = new ProcessStartInfo(Dotnet()) { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true }; start.ArgumentList.Add(PaoDll()); start.ArgumentList.Add("mcp");
    using var process = Process.Start(start)!; var lines = new List<string>();
    await SendAsync(requests[0]); lines.Add(await ReadLineAsync(process)); await SendAsync(requests[1]);
    await SendAsync(requests[2]); lines.Add(await ReadLineAsync(process));
    await SendAsync(requests[3]); lines.Add(await ReadLineAsync(process));
    await SendAsync(requests[4]); lines.Add(await ReadLineAsync(process));
    await SendAsync(requests[5]); lines.Add(await ReadLineAsync(process));
    await SendAsync(requests[6]); lines.Add(await ReadLineAsync(process));
    process.StandardInput.Close(); await process.WaitForExitAsync(); Equal(0, process.ExitCode); var stderr = await process.StandardError.ReadToEndAsync();
    foreach (var line in lines) { using var _ = JsonDocument.Parse(line); Check(!line.Contains('\n'), "embedded newline"); }
    var docs = lines.Select(x => JsonDocument.Parse(x)).ToArray();
    try {
      JsonElement ById(int value) => docs.Select(x => x.RootElement).Single(x => x.GetProperty("id").GetInt32() == value);
      Equal("2026-07-28", ById(1).GetProperty("result").GetProperty("protocolVersion").GetString());
      var tools=ById(2).GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();Equal(1,tools.Length);Equal("pao",tools[0].GetProperty("name").GetString());var schema=tools[0].GetProperty("inputSchema");Equal(false,schema.GetProperty("additionalProperties").GetBoolean());var schemaProperties=schema.GetProperty("properties");var operations=schemaProperties.GetProperty("op").GetProperty("enum").EnumerateArray().Select(x=>x.GetString()).ToArray();Check(operations.Contains("cp",StringComparer.Ordinal),"unified operation enum missing cp");Check(operations.Contains("edit",StringComparer.Ordinal)&&schemaProperties.TryGetProperty("line",out _),"unified operation schema missing line edit");Check(operations.Contains("read_lines",StringComparer.Ordinal)&&operations.Contains("encoding_inspect",StringComparer.Ordinal)&&operations.Contains("encoding_convert",StringComparer.Ordinal)&&operations.Contains("help",StringComparer.Ordinal)&&!operations.Contains("describe",StringComparer.Ordinal),"unified operation enum missing context/encoding/help operations or retained describe");Check(operations.Contains("feedback_submit",StringComparer.Ordinal)&&operations.Contains("feedback_export",StringComparer.Ordinal),"unified operation enum missing feedback");Check(schemaProperties.TryGetProperty("summary",out _)&&schemaProperties.TryGetProperty("status",out _)&&schemaProperties.TryGetProperty("after",out _),"feedback schema fields missing");var annotations=tools[0].GetProperty("annotations");Equal(true,annotations.GetProperty("destructiveHint").GetBoolean());Equal(false,annotations.GetProperty("openWorldHint").GetBoolean());
      Equal(-32602, ById(4).GetProperty("error").GetProperty("code").GetInt32()); Check(ById(5).TryGetProperty("result", out _), "MCP died after tool error");Equal(-32602,ById(6).GetProperty("error").GetProperty("code").GetInt32());
    } finally { foreach (var doc in docs) doc.Dispose(); }
    Equal(string.Empty, stderr.Trim());

    async Task SendAsync(string value) { await process.StandardInput.WriteLineAsync(value); await process.StandardInput.FlushAsync(); }
    static async Task<string> ReadLineAsync(Process child) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); return await child.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException(); }
}

static async Task McpGitCwdDetailAsync()
{
    using var temp=new TempRoot();

    var requestedCwd=temp.Dir("requested workspace");var capture=new CaptureDispatcher();
    var detailFalse=await CallAsync(capture,new{op="git_status",cwd=requestedCwd,detail=false,rid="detail-false"});Check(detailFalse.GetProperty("result").TryGetProperty("structuredContent",out _),detailFalse.ToString());Equal(false,capture.Last!.Detail);Equal(Path.GetFullPath(requestedCwd),capture.Last.Cwd);
    var detailTrue=await CallAsync(capture,new{op="git_status",cwd=requestedCwd,detail=true,rid="detail-true"});Check(detailTrue.GetProperty("result").TryGetProperty("structuredContent",out _),detailTrue.ToString());Equal(true,capture.Last!.Detail);Equal(Path.GetFullPath(requestedCwd),capture.Last.Cwd);
    var workspaceScoped=await CallAsync(capture,new{op="git_status",ws="exchange-source",detail=false,rid="workspace-scoped"});Check(workspaceScoped.GetProperty("result").TryGetProperty("structuredContent",out _),workspaceScoped.ToString());Equal("exchange-source",capture.Last!.Ws);Equal<string?>(null,capture.Last.Cwd);
    var combined=await CallAsync(capture,new{op="git_status",ws="exchange-source",cwd="."});Equal(-32602,combined.GetProperty("error").GetProperty("code").GetInt32());Check(combined.GetProperty("error").GetProperty("message").GetString()!.Contains("cwd omitted",StringComparison.Ordinal),combined.ToString());
    var aliasCwd=await CallAsync(capture,new{op="git_status",cwd="exchange-source:."});Equal(-32602,aliasCwd.GetProperty("error").GetProperty("code").GetInt32());Check(aliasCwd.GetProperty("error").GetProperty("message").GetString()!.Contains("do not put an alias",StringComparison.Ordinal),aliasCwd.ToString());
    var unknown=await CallAsync(capture,new{op="git_status",cwd=requestedCwd,unknown_field="rejected"});Equal(-32602,unknown.GetProperty("error").GetProperty("code").GetInt32());
    var reservedRoot=await CallAsync(capture,new{op="git_status",cwd=requestedCwd,root=requestedCwd});Equal(-32602,reservedRoot.GetProperty("error").GetProperty("code").GetInt32());

    var missingState=temp.Dir("missing-state");await using(var missingEngine=new AgentOpsEngine(missingState)){var missing=await CallAsync(new EngineDispatcher(missingEngine),new{op="git_status",cwd=requestedCwd,detail=false,rid="missing-git"});AssertReceipt(missing,"missing-git","git_unconfigured");var receipt=missing.GetProperty("result").GetProperty("structuredContent");Equal(3,receipt.GetProperty("exitCode").GetInt32());Check(receipt.GetProperty("remedy").GetString()!.Contains("canonical Git executable",StringComparison.Ordinal),receipt.ToString());}

    var brokerState=temp.Dir("broker-state");var brokerWorkspace=temp.Dir("broker workspace");new WorkspaceRegistry(new AgentOpsPaths(brokerState)).Register("exchange-source",brokerWorkspace);OperationRequest? routed=null;var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,3);
    await using(var broker=new AuthenticatedServiceBroker(brokerState,owner,new RootCodeVerifier(Path.Combine(brokerState,"root.verifier")),false,(_,_,request,_)=>{routed=request;return Task.FromResult(new OperationReceipt(true,"inner",request.Rid!,"done",request.Op));}))
    {
        Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"git-contract-tray"),peer)).Ok,"fixture tray rejected");var routedReceipt=await broker.HandleUserAsync(new OperationRequest("git",new Dictionary<string,string>{{"op","status"}},"broker-workspace",Ws:"exchange-source",Cwd:null),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));Check(routedReceipt.Ok,routedReceipt.ToString());
    }
    Equal(Path.GetFullPath(brokerWorkspace),routed!.Cwd);Equal(Path.GetFullPath(brokerWorkspace),routed.PathRoots!["cwd"]);

    var cliCapture=new CaptureDispatcher();using(var cliOut=new StringWriter())using(var cliErr=new StringWriter()){Equal(0,await PaoProgram.RunCliAsync(["git","status","ws=exchange-source"],cliCapture,cliOut,cliErr,CancellationToken.None,TextReader.Null));Equal(string.Empty,cliErr.ToString());Equal("exchange-source",cliCapture.Last!.Ws);Equal<string?>(null,cliCapture.Last.Cwd);}

    var registeredState=temp.Dir("registered-state");var registeredWorkspace=temp.Dir("registered workspace");var argsPath=Path.Combine(temp.Path,"git-args.json");var cwdPath=Path.Combine(temp.Path,"git-cwd.txt");
    await RegisterToolAsync(registeredState,"git",FakeToolExe(),new Dictionary<string,string?>{{"PAO_FAKE_ARGS_PATH",argsPath},{"PAO_FAKE_CWD_PATH",cwdPath}});
    await using(var registeredEngine=new AgentOpsEngine(registeredState))
    {
        var executed=await CallAsync(new EngineDispatcher(registeredEngine),new{op="git_status",cwd=registeredWorkspace,detail=true,rid="registered-git"});var receipt=executed.GetProperty("result").GetProperty("structuredContent");Check(receipt.GetProperty("ok").GetBoolean(),receipt.ToString());Equal("registered-git",receipt.GetProperty("rid").GetString());
        Equal(new[]{"status"},JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argsPath))!);Equal(Path.GetFullPath(registeredWorkspace),Path.GetFullPath(await File.ReadAllTextAsync(cwdPath)));
    }

    var outside=temp.Dir("outside workspace");Throws("precondition",()=>new ToolRecipeOperations(registeredState).PrepareGit("status",workspaceRoot:registeredWorkspace,cwd:outside));Equal(Path.GetFullPath(registeredWorkspace),Path.GetFullPath(await File.ReadAllTextAsync(cwdPath)));

    static void AssertReceipt(JsonElement response,string rid,string error)
    {
        Check(!response.TryGetProperty("error",out _),response.ToString());var receipt=response.GetProperty("result").GetProperty("structuredContent");Equal(rid,receipt.GetProperty("rid").GetString());Equal(error,receipt.GetProperty("err").GetString());Check(receipt.GetProperty("jid").GetString() is{Length:>0},"missing jid");Equal(false,receipt.GetProperty("partial").GetBoolean());
    }
    static async Task<JsonElement> CallAsync(IOperationDispatcher dispatcher,object arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="git-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(10));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task McpDotnetSolutionAsync()
{
    using var temp=new TempRoot();var workspace=temp.Dir("dotnet workspace");var solution=Path.Combine(workspace,"Pegasus App Ω.slnx");await File.WriteAllTextAsync(solution,"fixture");var capture=new CaptureDispatcher();
    var listed=await ListToolsAsync(capture);var properties=listed.GetProperty("result").GetProperty("tools")[0].GetProperty("inputSchema").GetProperty("properties");var sourceDescription=properties.GetProperty("src").GetProperty("description").GetString()!;var workspaceDescription=properties.GetProperty("ws").GetProperty("description").GetString()!;Check(sourceDescription.Contains("solution or project",StringComparison.Ordinal),sourceDescription);Check(workspaceDescription.Contains("build root",StringComparison.Ordinal),workspaceDescription);Check(properties.TryGetProperty("cfg",out _)&&properties.TryGetProperty("dst",out _),"dotnet configuration or publish output missing from advertised schema");Equal("seconds",properties.GetProperty("tmo").GetProperty("description").GetString());
    var parsed=await CallAsync(capture,new{op="dotnet_build",src="Pegasus App Ω.slnx",cfg="Release",ws="agentops",arg=new[]{"--no-restore"},tmo=120,detail=true,rid="dotnet-typed"});Check(parsed.GetProperty("result").TryGetProperty("structuredContent",out _),parsed.ToString());Equal("dotnet",capture.Last!.Op);Equal("build",capture.Last.Arg["op"]);Equal("Release",capture.Last.Arg["cfg"]);Equal("Pegasus App Ω.slnx",capture.Last.Arg["src"]);Equal("--no-restore",capture.Last.Arg["arg0"]);Equal("agentops",capture.Last.Ws);Equal<string?>(null,capture.Last.Cwd);Equal(true,capture.Last.Detail);
    var context=await CallAsync(capture,new{op="ctx_ls",detail=true,rid="ctx-detail"});Check(context.GetProperty("result").TryGetProperty("structuredContent",out _),context.ToString());Equal(true,capture.Last!.Detail);
    var unknown=await CallAsync(capture,new{op="dotnet_build",src="Pegasus App Ω.slnx",project="wrong-field"});Equal(-32602,unknown.GetProperty("error").GetProperty("code").GetInt32());var unknownMessage=unknown.GetProperty("error").GetProperty("message").GetString()!;Check(unknownMessage.Contains("'project'",StringComparison.Ordinal)&&unknownMessage.Contains("src=<solution-or-project>",StringComparison.Ordinal),unknownMessage);
    var invalidConfiguration=await CallAsync(capture,new{op="dotnet_build",src="Pegasus App Ω.slnx",cfg=42});Equal(-32602,invalidConfiguration.GetProperty("error").GetProperty("code").GetInt32());Check(invalidConfiguration.GetProperty("error").GetProperty("message").GetString()!.Contains("'cfg'",StringComparison.Ordinal),invalidConfiguration.ToString());
    var invalidTimeout=await CallAsync(capture,new{op="dotnet_build",src="Pegasus App Ω.slnx",tmo=0});Equal(-32602,invalidTimeout.GetProperty("error").GetProperty("code").GetInt32());Check(invalidTimeout.GetProperty("error").GetProperty("message").GetString()!.Contains("'tmo'",StringComparison.Ordinal),invalidTimeout.ToString());

    var cliCapture=new CaptureDispatcher();using(var output=new StringWriter())using(var error=new StringWriter()){Equal(0,await PaoProgram.RunCliAsync(["dotnet","op=build","src=Pegasus App Ω.slnx","cfg=Release","ws=agentops"],cliCapture,output,error,CancellationToken.None));Equal(string.Empty,error.ToString());Equal("Pegasus App Ω.slnx",cliCapture.Last!.Arg["src"]);Equal("agentops",cliCapture.Last.Ws);Equal<string?>(null,cliCapture.Last.Cwd);}

    var state=temp.Dir("dotnet-state");var argsPath=Path.Combine(temp.Path,"dotnet-args.json");var cwdPath=Path.Combine(temp.Path,"dotnet-cwd.txt");await RegisterToolAsync(state,"dotnet",FakeToolExe(),new Dictionary<string,string?>{{"PAO_FAKE_ARGS_PATH",argsPath},{"PAO_FAKE_CWD_PATH",cwdPath},{"PAO_FAKE_DOTNET_PUBLISH","1"}});
    await using(var engine=new AgentOpsEngine(state))
    {
        var executed=await CallAsync(new EngineDispatcher(engine),new{op="dotnet_build",src=solution,cfg="Release",cwd=workspace,arg=new[]{"--no-restore"},rid="dotnet-exec"});var receipt=executed.GetProperty("result").GetProperty("structuredContent");Check(receipt.GetProperty("ok").GetBoolean(),receipt.ToString());Equal("dotnet_build",receipt.GetProperty("op").GetString());Equal(new[]{"build",solution,"--configuration","Release","--no-restore"},JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argsPath))!);
        var publish=Path.Combine(workspace,"publish output 雪 &");var published=await CallAsync(new EngineDispatcher(engine),new{op="dotnet_publish",src=solution,dst=publish,cfg="Release",cwd=workspace,rid="dotnet-publish"});var publishReceipt=published.GetProperty("result").GetProperty("structuredContent");Check(publishReceipt.GetProperty("ok").GetBoolean(),publishReceipt.ToString());Equal("dotnet_publish",publishReceipt.GetProperty("op").GetString());Equal(new[]{"publish",solution,"--configuration","Release","--output",publish},JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argsPath))!);
        var missing=await CallAsync(new EngineDispatcher(engine),new{op="dotnet_build",src=Path.Combine(workspace,"missing.csproj"),cwd=workspace,rid="dotnet-missing"});var missingReceipt=missing.GetProperty("result").GetProperty("structuredContent");Equal("not_found",missingReceipt.GetProperty("err").GetString());Equal("src",missingReceipt.GetProperty("data").GetProperty("field").GetString());
    }
    Equal(Path.GetFullPath(workspace),Path.GetFullPath(await File.ReadAllTextAsync(cwdPath)));
    var emptyState=temp.Dir("dotnet-empty-publish-state");await RegisterToolAsync(emptyState,"dotnet",FakeToolExe());
    await using(var emptyEngine=new AgentOpsEngine(emptyState))
    {
        var emptyOutput=Path.Combine(workspace,"empty publish output");
        var empty=await CallAsync(new EngineDispatcher(emptyEngine),new{op="dotnet_publish",src=solution,dst=emptyOutput,cwd=workspace,rid="dotnet-empty-publish"});
        var emptyReceipt=empty.GetProperty("result").GetProperty("structuredContent");Equal("external_failed",emptyReceipt.GetProperty("err").GetString());Check(!Directory.Exists(emptyOutput)||!Directory.EnumerateFiles(emptyOutput,"*",SearchOption.AllDirectories).Any(),emptyReceipt.ToString());
    }

    static async Task<JsonElement> CallAsync(IOperationDispatcher dispatcher,object arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="dotnet-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(10));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
    static async Task<JsonElement> ListToolsAsync(IOperationDispatcher dispatcher)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="dotnet-schema",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/list",@params=new{}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(10));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task McpProcessListFilterAsync()
{
    using var temp=new TempRoot();var pattern="Pegasus.AgentOps.FakeTool";var capture=new CaptureDispatcher();
    var parsed=await CallAsync(capture,new{op="proc_ls",pattern,max=2,rid="proc-filter-parse"});Check(parsed.GetProperty("result").TryGetProperty("structuredContent",out _),parsed.ToString());Equal(pattern,capture.Last!.Arg["pattern"]);Equal("2",capture.Last.Arg["max"]);
    var blank=await CallAsync(capture,new{op="proc_ls",pattern=" ",max=2});Equal(-32602,blank.GetProperty("error").GetProperty("code").GetInt32());var wrongOp=await CallAsync(capture,new{op="proc_inspect",pid=Environment.ProcessId,pattern});Equal(-32602,wrongOp.GetProperty("error").GetProperty("code").GetInt32());

    var children=new List<Process>();
    try
    {
        for(var i=0;i<3;i++)
        {
            var start=new ProcessStartInfo(FakeToolExe()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};SetExplicitEnvironment(start,MinimumToolEnvironment(FakeToolExe()));start.Environment["PAO_FAKE_ARGS_PATH"]=Path.Combine(temp.Path,$"fixture-{i}.json");start.ArgumentList.Add("--wait-ms=10000");children.Add(Process.Start(start)??throw new Exception("fixture process did not start"));
        }
        await Task.Delay(150);Check(children.All(child=>!child.HasExited),"fixture process exited early");
        await using var engine=new AgentOpsEngine(temp.Dir("state"));var response=await CallAsync(new EngineDispatcher(engine),new{op="proc_ls",pattern,max=2,rid="proc-filter-live"});var receipt=response.GetProperty("result").GetProperty("structuredContent");Check(receipt.GetProperty("ok").GetBoolean(),receipt.ToString());var data=receipt.GetProperty("data");var processes=data.GetProperty("processes").EnumerateArray().ToArray();Equal(2,processes.Length);Check(data.GetProperty("matched").GetInt32()>=3,"fixture matches missing");Equal(true,data.GetProperty("truncated").GetBoolean());Check(processes.All(item=>item.GetProperty("name").GetString()!.Contains(pattern,StringComparison.OrdinalIgnoreCase)),"unfiltered process returned");var pids=processes.Select(item=>item.GetProperty("pid").GetInt32()).ToArray();Equal(pids.OrderBy(pid=>pid).ToArray(),pids);
    }
    finally
    {
        foreach(var child in children){if(!child.HasExited)child.Kill(true);child.WaitForExit();child.Dispose();}
    }

    static async Task<JsonElement> CallAsync(IOperationDispatcher dispatcher,object arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="proc-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(10));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task UiParserContractAsync()
{
    var catalog=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root(),"examples","ui-automation","requests.json"))).RootElement;
    foreach(var example in catalog.GetProperty("requests").EnumerateArray())
    {
        var valid=example.GetProperty("valid").GetBoolean();var dispatcher=new CaptureDispatcher();
        var response=await CallAsync(dispatcher,example.GetProperty("input"));
        Check(response.TryGetProperty(valid?"result":"error",out var branch),$"example {example.GetProperty("name").GetString()} validity drifted: {response}");
        if(!valid)Equal(-32602,branch.GetProperty("code").GetInt32());
    }

    var cli=new CaptureDispatcher();using var stdout=new StringWriter();using var stderr=new StringWriter();
    Equal(0,await PaoProgram.RunCliAsync(["ui","set-text","0x12345","4242","2026-08-27T20:15:30.1234567Z","0x12350","tmo=5","--stdin"],cli,stdout,stderr,CancellationToken.None,new StringReader("CLI fixture Ω &")));
    Equal("ui",cli.Last!.Op);Equal("set_text",cli.Last.Arg["op"]);Equal("CLI fixture Ω &",cli.Last.Body);Equal("0x12345",cli.Last.Arg["hwnd"]);
    stdout.GetStringBuilder().Clear();stderr.GetStringBuilder().Clear();
    Equal(0,await PaoProgram.RunCliAsync(["ui","capture","0x12345","4242","2026-08-27T20:15:30.1234567Z","captures/window.png","ws=project","fmt=png","tmo=5"],cli,stdout,stderr,CancellationToken.None));
    Equal("capture",cli.Last!.Arg["op"]);Equal("captures/window.png",cli.Last.Arg["dst"]);Equal("project",cli.Last.Ws);
    stdout.GetStringBuilder().Clear();stderr.GetStringBuilder().Clear();
    Equal(0,await PaoProgram.RunCliAsync(["help","ui_find"],cli,stdout,stderr,CancellationToken.None));
    Check(stdout.ToString().Contains("signed-in",StringComparison.OrdinalIgnoreCase)&&stdout.ToString().Contains("1..30",StringComparison.Ordinal),stdout.ToString());

    static async Task<JsonElement> CallAsync(IOperationDispatcher dispatcher,JsonElement arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);
        reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="ui-contract",version="1"}}}));
        reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));
        reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));
        var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(5));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task NsisAdapterAsync()
{
    using var temp=new TempRoot();var state=temp.Dir("nsis-state");var script=Path.Combine(temp.Path,"AgentOps setup Ω.nsi");var output=Path.Combine(temp.Path,"AgentOps Setup.exe");var argsPath=Path.Combine(temp.Path,"nsis-args.json");
    var largeSafeBody=string.Concat(Enumerable.Repeat("; bounded safe NSIS comment 0123456789abcdef\n",3000));
    await File.WriteAllTextAsync(script,"Unicode true\nOutFile \"${PAO_OUT}\"\n"+largeSafeBody+"Section\nSectionEnd\n",new UTF8Encoding(false));
    RegisteredToolDefinition admittedNsis;var nsisExe=Path.GetFullPath(FakeToolExe());using(var stream=File.OpenRead(nsisExe)){var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();var environment=new Dictionary<string,string?>(MinimumToolEnvironment(nsisExe),StringComparer.OrdinalIgnoreCase){{"PAO_FAKE_NSIS","1"},{"PAO_FAKE_ARGS_PATH",argsPath}};var tools=new ToolRecipeOperations(state);admittedNsis=await tools.Tools.RegisterAsync(new RegisteredToolDefinition("nsis",1,nsisExe,sha,TimeoutSeconds:3600,ExpectedExitCodes:[0],MaxArguments:32,MaxOutputBytes:64*1024,VersionProbe:new ToolProbeDefinition(["/?"],[0],"MakeNSIS"),Environment:environment));}
    var fixtureOperations=new ToolRecipeOperations(state);var fixturePreprocess=fixtureOperations.PrepareTool("nsis",new[]{"/SAFEPPO","/V4","/WX","/NOCONFIG","/OUTPUTCHARSET","UTF8","/DPAO_OUT="+output,script},cwd:temp.Path,timeoutSeconds:30);var fixtureExpanded=await fixtureOperations.RunAsync(fixturePreprocess,CancellationToken.None);Check(fixtureExpanded.Stdout.Contains(output,StringComparison.Ordinal),$"fixture preprocess did not expand destination: {fixtureExpanded.Stdout}");
    await using var engine=new AgentOpsEngine(state);var dispatcher=new EngineDispatcher(engine);
    var validate=await engine.ExecuteAsync(new OperationRequest("nsis",new Dictionary<string,string>{{"op","validate"},{"src",script},{"tmo","30"}},"nsis-validate",Cwd:temp.Path));Check(validate.Ok,$"{validate.Err} {JsonSerializer.Serialize(validate.Data)}");Equal("nsis_validate",validate.Op);Equal(true,Convert.ToBoolean(validate.Data!["validated"]));Check(Convert.ToInt32(validate.Data["lines"])>=4,"NSIS expanded line count missing");Check(Convert.ToString(validate.Data["sha"])?.Length==64,"NSIS expanded hash missing");
    using var stdout=new StringWriter();using var stderr=new StringWriter();var exit=await PaoProgram.RunCliAsync(["nsis","build",script,output,"tmo=30","rid=nsis-build"],dispatcher,stdout,stderr,CancellationToken.None);Check(exit==0,$"NSIS CLI exit={exit} stdout={stdout} stderr={stderr}");Equal(string.Empty,stderr.ToString().Trim());Equal("1",Field(stdout.ToString(),"ok"));Check(!stdout.ToString().Contains("fixture compile ok",StringComparison.Ordinal),"normal NSIS output leaked the compiler log");Check(File.Exists(output),"fixture NSIS output missing");var argv=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argsPath))!;Equal(new[]{"/V4","/WX","/NOCONFIG","/OUTPUTCHARSET","UTF8","/DPAO_OUT="+output,script},argv);
    var unsafeScript=Path.Combine(temp.Path,"unsafe.nsi");await File.WriteAllTextAsync(unsafeScript,"!system 'whoami'\nOutFile \"${PAO_OUT}\"\nSection\nSectionEnd\n");var unsafeReceipt=await engine.ExecuteAsync(new OperationRequest("nsis",new Dictionary<string,string>{{"op","validate"},{"src",unsafeScript},{"tmo","30"}},"nsis-unsafe",Cwd:temp.Path));Equal("nsis_unsafe_directive",unsafeReceipt.Err);Equal(false,unsafeReceipt.Partial);
    var capture=new CaptureDispatcher();var mcp=await CallNsisAsync(capture,new{op="nsis_validate",src=script,cwd=temp.Path,tmo=30,detail=true});Check(mcp.TryGetProperty("result",out _),mcp.ToString());Equal("nsis",capture.Last!.Op);Equal("validate",capture.Last.Arg["op"]);Equal(script,capture.Last.Arg["src"]);var rejected=await CallNsisAsync(capture,new{op="nsis_build",src=script,dst=output,arg=new[]{"/XFile /etc/passwd"}});Equal(-32602,rejected.GetProperty("error").GetProperty("code").GetInt32());
    using var missingTemp=new TempRoot();await using var missingEngine=new AgentOpsEngine(missingTemp.Dir("state"));var missing=await missingEngine.ExecuteAsync(new OperationRequest("nsis",new Dictionary<string,string>{{"op","status"}},"nsis-missing"));Equal("nsis_unconfigured",missing.Err);
    using var productTemp=new TempRoot();await using var productEngine=new AgentOpsEngine(productTemp.Dir("state"),bundledNsis:admittedNsis);var productStatus=await productEngine.ExecuteAsync(new OperationRequest("nsis",new Dictionary<string,string>{{"op","status"}},"nsis-product"));Check(productStatus.Ok,productStatus.Err??"product NSIS definition failed");Equal("product",Convert.ToString(productStatus.Data!["provider"]));
    using var bundleTemp=new TempRoot();var bundleRoot=bundleTemp.Dir("product");var bundleState=bundleTemp.Dir(Path.Combine("product","state","users","S-1-5-21-100","execution"));var bundleCompiler=Path.Combine(bundleRoot,"tools","nsis","3.12","Bin","makensis.exe");Directory.CreateDirectory(Path.GetDirectoryName(bundleCompiler)!);File.Copy(nsisExe,bundleCompiler);var admittedSha=ToolRecipeValidation.ComputeFileHash(bundleCompiler);var admissionPath=Path.Combine(bundleRoot,"tools","nsis","3.12","pegasus-nsis-admission.v1.json");await File.WriteAllTextAsync(admissionPath,JsonSerializer.Serialize(new{contract="pegasus-cortex-bundled-nsis",version=1,nsisVersion="3.12",archiveSha256="56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f",compilerRelativePath="Bin/makensis.exe",upstreamCompilerSha256="25d1aa7081db1a9de9690b59983f9652b7409a189c3444328cc1841eff693e8d",admittedCompilerSha256=admittedSha}));var bundled=await BundledNsisTool.AdmitAsync(bundleRoot,bundleState,runner:new NsisVersionRunner());Check(bundled.ProbeVerified,"the product NSIS admission was not probe verified");Equal(admittedSha,bundled.AdmissionSha256);Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("ComSpec")!),bundled.Environment!["ComSpec"]);await File.AppendAllTextAsync(bundleCompiler,"tampered");await ThrowsAgentOpsAsync("integrity",()=>BundledNsisTool.AdmitAsync(bundleRoot,bundleState,runner:new NsisVersionRunner()));

    static async Task<JsonElement> CallNsisAsync(IOperationDispatcher dispatcher,object arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="nsis-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(5));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task ThrowsAgentOpsAsync(string code,Func<Task> action){try{await action();throw new Exception("expected "+code);}catch(AgentOpsException ex){Equal(code,ex.Code);}}

static async Task UiServiceBoundaryAsync()
{
    using var temp=new TempRoot();var serviceState=temp.Dir("ui-service-state");var trayState=temp.Dir("ui-tray-state");var owner="S-1-5-21-4242";var peer=new LocalPeer(owner,7);var secretTitle="UI selector secret Ω & "+Guid.NewGuid().ToString("N");OperationRequest? routed=null;
    await using(var engine=new AgentOpsEngine(trayState,directOnly:true))
    await using(var broker=new AuthenticatedServiceBroker(serviceState,owner,new RootCodeVerifier(Path.Combine(serviceState,"root.verifier")),false,async(_,_,request,ct)=>{routed=request;if(request.Arg.GetValueOrDefault("op")=="status")return await engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct);return new OperationReceipt(true,"inner",request.Rid!,"done",OperationNames.Public(request),Data:new Dictionary<string,object?>{{"title",secretTitle},{"text",secretTitle},{"hwnd","0x1"}});} ))
    {
        var directStatus=await engine.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("ui",new Dictionary<string,string>{{"op","status"}},"ui-direct-status"));Check(directStatus.Ok,$"direct UI status {directStatus.State}/{directStatus.Err}");
        var waiting=await broker.HandleUserAsync(new OperationRequest("ui",new Dictionary<string,string>{{"op","find"},{"title",secretTitle},{"match","exact"},{"tmo","5"}},"ui-secret"),peer,CancellationToken.None);Equal("waiting_user",waiting.State);Check(!StateContainsAscii(serviceState,secretTitle),"UI selector persisted before tray registration");
        Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,7,"ui-fixture"),peer)).Ok,"UI tray registration failed");await WaitRouterStateAsync(serviceState,"ui-secret","done");Equal(secretTitle,routed!.Arg["title"]);Check(!StateContainsAscii(serviceState,secretTitle),"UI selector or response text persisted after completion");
        var stored=await broker.HandleUserAsync(new OperationRequest("job",new Dictionary<string,string>{{"op","stat"},{"jid",waiting.Jid}},"ui-stat"),peer,CancellationToken.None);Check(stored.Data is null||!stored.Data.ContainsKey("title")&&!stored.Data.ContainsKey("text"),"durable UI receipt retained live text");
        var directStatusAgain=await engine.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("ui",new Dictionary<string,string>{{"op","status"}},"ui-direct-status-2"));Check(directStatusAgain.Ok,$"second direct UI status {directStatusAgain.State}/{directStatusAgain.Err}");
        var capturePath=Path.Combine(temp.Path,"capture.png");var capture=await broker.HandleUserAsync(new OperationRequest("ui",new Dictionary<string,string>{{"op","capture"},{"hwnd","0x1"},{"pid","42"},{"start","2026-08-27T20:15:30.1234567Z"},{"dst",capturePath},{"fmt","png"},{"tmo","5"}},"ui-capture-freeze"),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));Check(capture.Ok,capture.Err??capture.State);Equal(Path.GetFullPath(capturePath),routed!.Arg["dst"]);Check(routed.PathRoots is not null,"capture path admission metadata was lost");
        var status=await broker.HandleUserAsync(new OperationRequest("ui",new Dictionary<string,string>{{"op","status"}},"ui-status"),peer,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));Check(status.Ok,$"{status.Err} {JsonSerializer.Serialize(status.Data)}");Equal(true,Convert.ToBoolean(status.Data!["available"]));
        var denied=await broker.HandleSystemAsync(new OperationRequest("ui",new Dictionary<string,string>{{"op","status"}},"ui-system",Root:"AB12"),peer,CancellationToken.None);Equal("auth",denied.Err);
    }
}

static async Task UiBrokerEquivalenceAsync()
{
    using var temp=new TempRoot();var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,Math.Max(1,LocalIdentity.Current().Session));await using var engine=new AgentOpsEngine(temp.Dir("ui-broker-tray"),directOnly:true);await using var broker=new AuthenticatedServiceBroker(temp.Dir("ui-broker-service"),owner,new RootCodeVerifier(Path.Combine(temp.Path,"root.verifier")),false,(_,_,request,ct)=>engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct));Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,peer.Session,"ui-equivalence"),peer)).Ok,"fixture tray rejected");var dispatcher=new BrokerDispatcher(broker,peer);
    using var cliOut=new StringWriter();using var cliErr=new StringWriter();Equal(0,await PaoProgram.RunCliAsync(["ui","status","rid=ui-cli-broker"],dispatcher,cliOut,cliErr,CancellationToken.None));Equal(string.Empty,cliErr.ToString().Trim());Equal("1",Field(cliOut.ToString(),"ok"));Equal("true",Field(cliOut.ToString(),"available"));
    using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="ui-broker",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=new{op="ui_status",rid="ui-mcp-broker"}}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(10));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);var receipt=response.RootElement.GetProperty("result").GetProperty("structuredContent");Check(receipt.GetProperty("ok").GetBoolean(),receipt.ToString());Equal("ui_status",receipt.GetProperty("op").GetString());var data=receipt.GetProperty("data");Equal(true,data.GetProperty("available").GetBoolean());Equal(Uri.UnescapeDataString(Field(cliOut.ToString(),"provider")),data.GetProperty("provider").GetString());Equal(Uri.UnescapeDataString(Field(cliOut.ToString(),"architecture")),data.GetProperty("architecture").GetString());
}

static async Task FeedbackInboxAsync()
{
    using var temp=new TempRoot();var serviceState=temp.Dir("feedback-service");var trayState=temp.Dir("feedback-tray");var owner=LocalIdentity.Current().Sid;var peer=new LocalPeer(owner,3);string fid;
    await using(var engine=new AgentOpsEngine(trayState,directOnly:true))
    await using(var broker=new AuthenticatedServiceBroker(serviceState,owner,new RootCodeVerifier(Path.Combine(serviceState,"root.verifier")),false,(_,_,request,ct)=>engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct)))
    {
        Check((await broker.RegisterTrayAsync(new TrayRegistration(owner,3,"feedback-tray"),peer)).Ok,"feedback tray rejected");
        var dispatcher=new BrokerDispatcher(broker,peer);var submitted=await CallAsync(dispatcher,new{op="feedback_submit",kind="improvement",area="mcp.schema",summary="Return a field-specific remedy Ω & brackets [x]",notes="Observed with a conventional request; no secrets.",rid="feedback-submit"});
        var receipt=submitted.GetProperty("result").GetProperty("structuredContent");Check(receipt.GetProperty("ok").GetBoolean(),receipt.ToString());Check(!submitted.ToString().Contains("Observed with a conventional request",StringComparison.Ordinal),"submit receipt exposed full notes");var submitData=receipt.GetProperty("data");Equal(true,submitData.GetProperty("notes_recorded").GetBoolean());Check(submitData.GetProperty("notes_bytes").GetInt32()>0,"missing bounded notes acknowledgement");fid=submitData.GetProperty("fid").GetString()!;Check(System.Text.RegularExpressions.Regex.IsMatch(fid,"^F[0-9A-F]{16}$"),fid);
        var duplicate=await CallAsync(dispatcher,new{op="feedback_submit",kind="bug",area="wrong",summary="must not replace original",rid="feedback-submit"});Equal(receipt.GetProperty("jid").GetString(),duplicate.GetProperty("result").GetProperty("structuredContent").GetProperty("jid").GetString());
        var listed=await CallAsync(dispatcher,new{op="feedback_ls",status="open",max=10,rid="feedback-list"});var listData=listed.GetProperty("result").GetProperty("structuredContent").GetProperty("data");Equal(1,listData.GetProperty("count").GetInt32());Equal(fid,listData.GetProperty("feedback")[0].GetProperty("fid").GetString());Check(listData.GetProperty("feedback")[0].GetProperty("notes").ValueKind==JsonValueKind.Null,"list exposed notes");
        var jobs=await CallAsync(dispatcher,new{op="job_ls",max=1,detail=true,rid="job-list-bounded"});var jobReceipt=jobs.GetProperty("result").GetProperty("structuredContent");Equal("job_ls",jobReceipt.GetProperty("op").GetString());Check(jobReceipt.GetProperty("data").GetProperty("jobs").GetArrayLength()<=1,"job list ignored max");
        var wrongJobField=await CallAsync(dispatcher,new{op="job_ls",count=1});Equal(-32602,wrongJobField.GetProperty("error").GetProperty("code").GetInt32());Check(wrongJobField.GetProperty("error").GetProperty("message").GetString()!.Contains("'count'",StringComparison.Ordinal),wrongJobField.ToString());
        var tools=await CallAsync(dispatcher,new{op="tool_ls",rid="tool-discovery"});var toolReceipt=tools.GetProperty("result").GetProperty("structuredContent");Equal("tool_ls",toolReceipt.GetProperty("op").GetString());Check(toolReceipt.GetProperty("data").TryGetProperty("tools",out _),toolReceipt.ToString());
        var recipes=await CallAsync(dispatcher,new{op="recipe_ls",rid="recipe-discovery"});var recipeReceipt=recipes.GetProperty("result").GetProperty("structuredContent");Equal("recipe_ls",recipeReceipt.GetProperty("op").GetString());Check(recipeReceipt.GetProperty("data").TryGetProperty("recipes",out _),recipeReceipt.ToString());
        var diag=await CallAsync(dispatcher,new{op="diag",detail=true,rid="identity-diag"});var diagReceipt=diag.GetProperty("result").GetProperty("structuredContent");Equal(AgentOpsProductInfo.ProductVersion,diagReceipt.GetProperty("data").GetProperty("product_version").GetString());Equal(AgentOpsProductInfo.McpSchemaVersion,diagReceipt.GetProperty("data").GetProperty("mcp_schema").GetInt32());
        var invalid=await CallAsync(dispatcher,new{op="feedback_submit",kind="wish",area="mcp",summary="invalid kind"});Equal(-32602,invalid.GetProperty("error").GetProperty("code").GetInt32());
        var unknown=await CallAsync(dispatcher,new{op="feedback_ls",unknown="rejected"});Equal(-32602,unknown.GetProperty("error").GetProperty("code").GetInt32());Check(unknown.GetProperty("error").GetProperty("message").GetString()!.Contains("'unknown'",StringComparison.Ordinal),unknown.ToString());
    }

    await using(var restarted=new AgentOpsEngine(trayState,directOnly:true))
    {
        var get=await restarted.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("feedback",new Dictionary<string,string>{{"op","get"},{"fid",fid}},"feedback-get"));Check(get.Ok,get.Err??"get failed");var item=(FeedbackItem)get.Data!["feedback"]!;Equal("Observed with a conventional request; no secrets.",item.Notes);Equal("open",item.Status);
        var second=await restarted.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("feedback",new Dictionary<string,string>{{"op","submit"},{"kind","feature"},{"area","cli"},{"summary","Add a review dashboard"}},"feedback-second"));Check(second.Ok,second.Err??"second submit failed");
        var changed=await restarted.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("feedback",new Dictionary<string,string>{{"op","set"},{"fid",fid},{"status","planned"}},"feedback-set"));Check(changed.Ok,changed.Err??"status failed");
        var export=await restarted.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("feedback",new Dictionary<string,string>{{"op","export"},{"max","1"}},"feedback-export"));Check(export.Ok,export.Err??"export failed");Equal(1,Convert.ToInt32(export.Data!["count"]));Equal(true,Convert.ToBoolean(export.Data["more"]));var bundleJson=JsonSerializer.Serialize(export.Data["bundle"],new JsonSerializerOptions(JsonSerializerDefaults.Web));Check(Encoding.UTF8.GetByteCount(bundleJson)<ProtocolVersion.MaxFrameBytes,"feedback export exceeded frame");using var bundle=JsonDocument.Parse(bundleJson);Equal(1,bundle.RootElement.GetProperty("version").GetInt32());Equal(fid,bundle.RootElement.GetProperty("items")[0].GetProperty("fid").GetString());Equal("planned",bundle.RootElement.GetProperty("items")[0].GetProperty("status").GetString());Check(bundle.RootElement.GetProperty("items")[0].GetProperty("notes").GetString()!.Contains("conventional request",StringComparison.Ordinal),bundleJson);
        var next=Convert.ToInt64(export.Data["next"]);var secondPage=await restarted.ExecuteAuthenticatedFrozenDirectAsync(new OperationRequest("feedback",new Dictionary<string,string>{{"op","export"},{"max","1"},{"after",next.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"feedback-export-next"));Check(secondPage.Ok,secondPage.Err??"second export failed");Equal(1,Convert.ToInt32(secondPage.Data!["count"]));Equal(false,Convert.ToBoolean(secondPage.Data["more"]));
    }

    await using(var cliEngine=new AgentOpsEngine(trayState))using(var output=new StringWriter())using(var error=new StringWriter()){Equal(0,await PaoProgram.RunCliAsync(["feedback","export","max=1"],new EngineDispatcher(cliEngine),output,error,CancellationToken.None));Equal(string.Empty,error.ToString());Check(output.ToString().Contains("fmt=pegasus-agentops-feedback%2F1",StringComparison.Ordinal)&&output.ToString().Contains("bundle=",StringComparison.Ordinal),output.ToString());}

    var cliCapture=new CaptureDispatcher();using(var output=new StringWriter())using(var error=new StringWriter()){Equal(0,await PaoProgram.RunCliAsync(["feedback","submit","feature","cli.parser","Support compact review export","notes=Exact CLI positional form","rid=feedback-cli"],cliCapture,output,error,CancellationToken.None));Equal(string.Empty,error.ToString());Equal("feedback",cliCapture.Last!.Op);Equal("submit",cliCapture.Last.Arg["op"]);Equal("feature",cliCapture.Last.Arg["kind"]);Equal("cli.parser",cliCapture.Last.Arg["area"]);Equal("Support compact review export",cliCapture.Last.Arg["summary"]);Equal("Exact CLI positional form",cliCapture.Last.Arg["notes"]);}

    static async Task<JsonElement> CallAsync(IOperationDispatcher dispatcher,object arguments)
    {
        using var reader=new McpLineReader();using var writer=new McpCaptureWriter();using var stderr=new StringWriter();var run=PaoProgram.RunMcpAsync(dispatcher,reader,writer,stderr,CancellationToken.None,defaultCwd:Environment.CurrentDirectory);reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2026-07-28",capabilities=new{},clientInfo=new{name="feedback-contract",version="1"}}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",method="notifications/initialized",@params=new{}}));reader.Send(JsonSerializer.Serialize(new{jsonrpc="2.0",id=2,method="tools/call",@params=new{name="pao",arguments=StructuredMcpArguments(arguments)}}));var line=await writer.Response.WaitAsync(TimeSpan.FromSeconds(10));reader.Complete();Equal(0,await run);Equal(string.Empty,stderr.ToString().Trim());using var response=JsonDocument.Parse(line);return response.RootElement.Clone();
    }
}

static async Task McpPipeDownAsync()
{
    var input = "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\",\"params\":{}}\n";
    var down = new RecordingDispatcher(request => new OperationReceipt(false,"down",request.Rid!,"failed",OperationNames.Public(request),"svc_down"));
    var privileged = new RecordingDispatcher(_ => throw new InvalidOperationException("ordinary MCP request entered the privileged service route"));
    await using var dispatcher = new RoutingDispatcher(down,privileged,privileged);
    var failed = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new{op="diag",output="structured"}),dispatcher,CancellationToken.None);
    Equal("user_host_unavailable",failed["structuredContent"]!["err"]!.GetValue<string>());
    using var reader = new StringReader(input);using var output = new StringWriter();using var error = new StringWriter();
    Equal(0,await PaoProgram.RunMcpAsync(dispatcher,reader,output,error,CancellationToken.None));
    var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);Equal(2,lines.Length);using var alive=JsonDocument.Parse(lines.Single(x=>x.Contains("\"id\":2",StringComparison.Ordinal)));Check(alive.RootElement.TryGetProperty("result",out _),"not alive");Equal(0,privileged.Count);
}

static async Task McpCancellationAsync()
{
    using var temp = new TempRoot(); var state = temp.Dir("state"); using var service = await StartServiceAsync(state);
    var start = new ProcessStartInfo(Dotnet()) { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true }; start.ArgumentList.Add(PaoDll()); start.ArgumentList.Add("mcp");
    using var process = Process.Start(start)!;
    await Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cancel-test\",\"version\":\"1\"}}}");
    _ = await Read(); await Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    var scriptJson = JsonSerializer.Serialize(DelayScript());
    await Send($"{{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{{\"name\":\"pao\",\"arguments\":{{\"op\":\"pwsh\",\"src\":{scriptJson},\"arg\":[\"5\"],\"tmo\":20,\"rid\":\"cancel-rid\"}}}}}}");
    await WaitForRidAsync(state,"cancel-rid");await WaitRouterStateAsync(state, "cancel-rid", "dispatching");
    await Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":10,\"reason\":\"test\"}}");
    await Send("{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"ping\",\"params\":{}}");
    var line = await Read(); Check(line.Contains("\"id\":11", StringComparison.Ordinal), "stale response emitted after cancellation: " + line);
    await WaitRouterStateAsync(state, "cancel-rid", "done"); process.StandardInput.Close(); await process.WaitForExitAsync(); Equal(0, process.ExitCode);

    async Task Send(string value) { await process.StandardInput.WriteLineAsync(value); await process.StandardInput.FlushAsync(); }
    async Task<string> Read() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); return await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException(); }
}

static async Task ServicePipeAsync()
{
    using var temp = new TempRoot();
    using var service = await StartServiceAsync(temp.Dir("state"));
    var result = await RunPaoAsync(["diag"]); Equal(0, result.ExitCode); Check(result.Stdout.StartsWith("ok=1", StringComparison.Ordinal), result.Stdout);
}

static async Task TrayProcessAsync()
{
    using var temp=new TempRoot();using var service=await StartServiceAsync(temp.Dir("state"));var result=await RunProcessAsync(Dotnet(),[TrayDll(),"--monitor-once"]);Equal(0,result.ExitCode);using var json=JsonDocument.Parse(result.Stdout);Check(json.RootElement.TryGetProperty("jobs",out _),result.Stdout);
}

static async Task TrayWinFormsSmokeAsync()
{
    var result=await RunProcessAsync(Dotnet(),[TrayDll(),"--ui-smoke"]);Equal(0,result.ExitCode);Check(string.IsNullOrWhiteSpace(result.Stderr),result.Stderr);
}

static Task UiHostResolutionAsync()
{
    using var temp=new TempRoot();var host=Directory.CreateDirectory(Path.Combine(temp.Path,"execution","host")).FullName;var ui=Directory.CreateDirectory(Path.Combine(temp.Path,"execution","ui")).FullName;
    File.WriteAllText(Path.Combine(host,"Pegasus.AgentOps.UiHost.exe"),"incomplete-nearer-apphost");
    foreach(var name in new[]{"Pegasus.AgentOps.UiHost.exe","Pegasus.AgentOps.UiHost.dll","Pegasus.AgentOps.UiHost.deps.json","Pegasus.AgentOps.UiHost.runtimeconfig.json","AutoItX3_x64.dll"})File.WriteAllText(Path.Combine(ui,name),name);
    Equal(Path.Combine(ui,"Pegasus.AgentOps.UiHost.exe"),UiAutomationClient.ResolveHost(host));
    return Task.CompletedTask;
}

static async Task TrayUiProviderSmokeAsync()
{
    var result=await RunProcessAsync(Dotnet(),[TrayDll(),"--ui-provider-status-once"]);
    Equal(0,result.ExitCode);Check(string.IsNullOrWhiteSpace(result.Stderr),result.Stderr);
    using var response=JsonDocument.Parse(result.Stdout);
    Equal(true,response.RootElement.GetProperty("Ok").GetBoolean());
    var data=response.RootElement.GetProperty("Data");
    Equal(true,data.GetProperty("available").GetBoolean());
    Equal("autoitx-registration-free",data.GetProperty("provider").GetString());
    Equal(true,data.GetProperty("interactiveUser").GetBoolean());
}

static async Task TrayUnavailableRetryAsync()
{
    using var temp=new TempRoot();var start=new ProcessStartInfo(Dotnet()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(TrayDll());start.ArgumentList.Add("--fixture-retry");start.Environment["PAO_TRAY_STATE_ROOT"]=temp.Dir("retry-tray");start.Environment["PAO_SERVICE_SID"]=LocalIdentity.Current().Sid;start.Environment["PAO_SERVICE_SESSION"]=LocalIdentity.Current().Session.ToString();using var process=Process.Start(start)!;try{await Task.Delay(3200);if(process.HasExited)throw new Exception("tray exited while the service was unavailable: "+await process.StandardError.ReadToEndAsync());}finally{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();}
}

static string Root()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if ((File.Exists(Path.Combine(current.FullName, "Pegasus.AgentOps.slnx")) ||
             File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx"))) &&
            File.Exists(Path.Combine(current.FullName, "START_HERE.md"))) return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("repo_root");
}
static string Dotnet()
{
    if(Environment.GetEnvironmentVariable("PAO_TEST_DOTNET") is {Length:>0} configured&&File.Exists(configured))return Path.GetFullPath(configured);if(Environment.GetEnvironmentVariable("DOTNET_ROOT") is {Length:>0} root&&File.Exists(Path.Combine(root,"dotnet.exe")))return Path.Combine(root,"dotnet.exe");try{return ResolveOnPath("dotnet.exe");}catch(FileNotFoundException){throw new FileNotFoundException(".NET 10 test host unavailable; set PAO_TEST_DOTNET to the verified dotnet.exe");}
}
static string Pwsh() => ResolveOnPath("pwsh.exe");
static string BuildConfiguration()
{
    var relative=Path.GetRelativePath(Root(),AppContext.BaseDirectory);
    var segments=relative.Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar],StringSplitOptions.RemoveEmptyEntries);
    var bin=Array.FindIndex(segments,segment=>string.Equals(segment,"bin",StringComparison.OrdinalIgnoreCase));
    return bin>=0&&bin+1<segments.Length?segments[bin+1]:"Debug";
}
static string PaoDll() => Path.Combine(Root(), "src", "pao", "bin", BuildConfiguration(), "net10.0-windows", "win-x64", "pao.dll");
static string ServiceDll() => Path.Combine(Root(), "src", "Pegasus.AgentOps.Service", "bin", BuildConfiguration(), "net10.0-windows", "win-x64", "PegasusCortex.ExecutionService.dll");
static string TrayDll() => Path.Combine(Root(), "src", "Pegasus.AgentOps.Tray", "bin", BuildConfiguration(), "net10.0-windows", "win-x64", "PegasusCortex.Host.dll");
static string FakeToolExe() => Path.Combine(Root(), "tests", "Pegasus.AgentOps.FakeTool", "bin", BuildConfiguration(), "net10.0-windows", "win-x64", "Pegasus.AgentOps.FakeTool.exe");
static string JunctionScript() => Path.Combine(Root(), "tests", "assets", "New-TestJunction.ps1");
static string DelayScript() => Path.Combine(Root(), "tests", "assets", "Delay.ps1");

static async Task<(int ExitCode,string Stdout,string Stderr)> RunProcessAsync(string file, IEnumerable<string> args, string? stdin = null, int closeDelayMs = 0)
{
    var start = new ProcessStartInfo(file) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true,StandardInputEncoding=new UTF8Encoding(false), CreateNoWindow=true };
    if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(file), "pwsh.exe")) SetExplicitEnvironment(start, MinimumToolEnvironment(file));
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    if (stdin is not null) { await process.StandardInput.WriteAsync(stdin); await process.StandardInput.FlushAsync(); if (closeDelayMs > 0) await Task.Delay(closeDelayMs); } process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
    return (process.ExitCode, await stdout, await stderr);
}

static Task<(int ExitCode,string Stdout,string Stderr)> RunPaoAsync(IEnumerable<string> args, string? stdin = null, int closeDelayMs = 0){var values=args.ToArray();return RunProcessAsync(Dotnet(),new[]{PaoDll()}.Concat(values),stdin,closeDelayMs==0&&stdin is not null&&values.Contains("mcp",StringComparer.Ordinal)?1000:closeDelayMs);}
static async Task<(int ExitCode,string Stdout,string Stderr)> RunPaoFromAsync(string cwd,IEnumerable<string> args)
{
    var start=new ProcessStartInfo(Dotnet()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=cwd};start.ArgumentList.Add(PaoDll());foreach(var arg in args)start.ArgumentList.Add(arg);using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));await process.WaitForExitAsync(timeout.Token);return(process.ExitCode,await stdout,await stderr);
}
static async Task<(int ExitCode,string Stdout,string Stderr)> RunPaoWithoutConsoleOverrideAsync(IEnumerable<string> args)
{
    var start=new ProcessStartInfo(Dotnet()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(PaoDll());foreach(var arg in args)start.ArgumentList.Add(arg);start.Environment.Remove("PAO_TEST_SERVICE_SID");start.Environment.Remove("PAO_TEST_SERVICE_SESSION");using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await process.WaitForExitAsync(timeout.Token);return(process.ExitCode,await stdout,await stderr);
}

static async Task<ServiceLease> StartServiceAsync(string state,string? dotnetTool=null,string? fakeArgsPath=null,bool startTray=true,bool crashAfterTray=false)
{
    var start = new ProcessStartInfo(Dotnet()) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
    start.ArgumentList.Add(ServiceDll()); start.ArgumentList.Add("--console-fixture"); start.Environment["PAO_STATE_ROOT"] = state; start.Environment["PAO_DOTNET_PATH"] = dotnetTool??Dotnet(); start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Dotnet())!;if(crashAfterTray)start.Environment["PAO_TEST_CRASH_AFTER_TRAY"]="1";
    var service = Process.Start(start)!; await Task.Delay(400); if (service.HasExited) throw new Exception("service failed: " + await service.StandardError.ReadToEndAsync());Process? tray=null;if(startTray){try{tray=await StartTrayProcessAsync(state,dotnetTool:dotnetTool,fakeArgs:fakeArgsPath);}catch{if(!service.HasExited)service.Kill(true);service.Dispose();throw;}}return new ServiceLease(service,tray);
}

static async Task<ProcessLease> StartTrayAsync(string state,string suffix="default",string? fakeCount=null,string? fakeArgs=null)=>new(await StartTrayProcessAsync(state,suffix,fakeCount,fakeArgs,fakeCount is null?null:FakeToolExe()));
static async Task<Process> StartTrayProcessAsync(string state,string suffix="default",string? fakeCount=null,string? fakeArgs=null,string? dotnetTool=null)
{
    var trayState=Path.Combine(state,"tray-"+suffix);Directory.CreateDirectory(trayState);await RegisterToolAsync(trayState,"pwsh",ResolveOnPath("pwsh.exe"));if(dotnetTool is not null){var toolEnvironment=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);if(fakeCount is not null)toolEnvironment["PAO_FAKE_COUNT_PATH"]=fakeCount;if(fakeArgs is not null)toolEnvironment["PAO_FAKE_ARGS_PATH"]=fakeArgs;await RegisterToolAsync(trayState,"dotnet",dotnetTool,toolEnvironment);}
    var start=new ProcessStartInfo(Dotnet()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(TrayDll());start.ArgumentList.Add("--fixture-identity");start.Environment["PAO_TRAY_STATE_ROOT"]=trayState;start.Environment["DOTNET_ROOT"]=Path.GetDirectoryName(Dotnet())!;var current=LocalIdentity.Current();start.Environment["PAO_SERVICE_SID"]=current.Sid;start.Environment["PAO_SERVICE_SESSION"]=current.Session.ToString();var tray=Process.Start(start)!;await Task.Delay(500);if(tray.HasExited)throw new Exception("tray failed: "+await tray.StandardError.ReadToEndAsync());return tray;
}
static async Task<(int ExitCode,string Stdout,string Stderr)> RunTrayToExitAsync(string state,IReadOnlyDictionary<string,string> environment)
{
    var start=new ProcessStartInfo(Dotnet()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(TrayDll());start.ArgumentList.Add("--fixture-identity");start.Environment["PAO_TRAY_STATE_ROOT"]=Path.Combine(state,"rejected-tray");start.Environment["DOTNET_ROOT"]=Path.GetDirectoryName(Dotnet())!;var current=LocalIdentity.Current();start.Environment["PAO_SERVICE_SID"]=current.Sid;start.Environment["PAO_SERVICE_SESSION"]=current.Session.ToString();foreach(var item in environment)start.Environment[item.Key]=item.Value;using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));await process.WaitForExitAsync(timeout.Token);return(process.ExitCode,await stdout,await stderr);
}

static void Throws(string code, Action action) { try { action(); throw new Exception("expected " + code); } catch (AgentOpsException ex) { Equal(code, ex.Code); } }
static async Task ThrowsAsync<T>(string message, Func<Task> action) where T:Exception { try { await action(); throw new Exception("expected " + typeof(T).Name); } catch (T ex) { Check(ex.Message.Contains(message, StringComparison.Ordinal), ex.Message); } }
// These operation-contract fixtures inspect persisted receipts. The projection
// fixtures separately exercise omitted-output and explicit agent presentation.
static System.Text.Json.Nodes.JsonObject StructuredMcpArguments(object arguments)
{
    var values = JsonSerializer.SerializeToNode(arguments)!.AsObject();
    if (!values.ContainsKey("output")) values["output"] = "structured";
    return values;
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (expected is Array ea && actual is Array aa) { if (!ea.Cast<object?>().SequenceEqual(aa.Cast<object?>())) throw new Exception($"expected [{string.Join(',',ea.Cast<object?>())}] actual [{string.Join(',',aa.Cast<object?>())}]"); } else if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected} actual {actual}"); }
static long LongField(string text, string name) => long.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(x => x.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
static string Field(string text,string name)=>text.Split(' ',StringSplitOptions.RemoveEmptyEntries).Single(x=>x.StartsWith(name+"=",StringComparison.Ordinal))[(name.Length+1)..].Trim();
static (string Sid,int Session,string State) ReadRouterIdentity(string state,string rid)
{
    using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"agentops-router.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT owner_sid,owner_session,state FROM routed_jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);using var reader=command.ExecuteReader();Check(reader.Read(),"router row missing");return(reader.GetString(0),reader.GetInt32(1),reader.GetString(2));
}
static string RouterJid(string state,string rid){using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"agentops-router.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT jid FROM routed_jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);return command.ExecuteScalar()?.ToString()??throw new Exception("router jid missing");}
static OperationRequest ReadRouterRequest(string state,string rid){using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"agentops-router.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT request FROM routed_jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);return JsonSerializer.Deserialize<OperationRequest>(command.ExecuteScalar()?.ToString()??throw new Exception("router request missing"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;}
static long SystemJobCount(string state)
{
    return EngineJobCount(Path.Combine(state,"system"));
}
static long EngineJobCount(string state){var db=Path.Combine(state,"agentops.db");if(!File.Exists(db))return 0;using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM jobs";return Convert.ToInt64(command.ExecuteScalar());}
static bool StateContainsAscii(string state,string value)
{
    var needle=Encoding.ASCII.GetBytes(value);foreach(var file in Directory.EnumerateFiles(state,"*",SearchOption.AllDirectories).Where(x=>Path.GetFileName(x).Contains(".db",StringComparison.OrdinalIgnoreCase)||x.EndsWith(".log",StringComparison.OrdinalIgnoreCase))){using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var memory=new MemoryStream();stream.CopyTo(memory);if(memory.GetBuffer().AsSpan(0,checked((int)memory.Length)).IndexOf(needle)>=0)return true;}return false;
}
static string GetJid(string state,string rid)
{
    using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"agentops.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT jid FROM jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);return command.ExecuteScalar()?.ToString()??throw new Exception("jid not found");
}
static async Task WaitForRidAsync(string state, string rid)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        foreach(var db in Directory.EnumerateFiles(state,"*.db",SearchOption.AllDirectories))try{using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText=Path.GetFileName(db)=="agentops-router.db"?"SELECT COUNT(*) FROM routed_jobs WHERE rid=$rid":"SELECT COUNT(*) FROM jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);if(Convert.ToInt64(command.ExecuteScalar())==1)return;}catch(SqliteException){ }
        await Task.Delay(25);
    }
    throw new TimeoutException("rid admission");
}
static async Task WaitForStateAsync(string state, string rid, string expected)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        foreach(var db in Directory.EnumerateFiles(state,"*.db",SearchOption.AllDirectories))try{using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText=Path.GetFileName(db)=="agentops-router.db"?"SELECT state FROM routed_jobs WHERE rid=$rid":"SELECT state FROM jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);if(command.ExecuteScalar()?.ToString()==expected)return;}catch(SqliteException){ }
        await Task.Delay(25);
    }
    throw new TimeoutException($"state {rid}={expected}");
}
static async Task WaitRouterStateAsync(string state,string rid,string expected)
{
    var deadline=Stopwatch.StartNew();while(deadline.Elapsed<TimeSpan.FromSeconds(10)){try{if(ReadRouterIdentity(state,rid).State==expected)return;}catch(Exception ex)when(ex is SqliteException or InvalidOperationException){}await Task.Delay(50);}throw new TimeoutException($"router {rid}={expected}");
}

static async Task RegisterToolAsync(string state,string name,string executable,IReadOnlyDictionary<string,string?>? environment=null)
{
    executable=Path.GetFullPath(executable);using var stream=File.OpenRead(executable);var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();var toolEnvironment=new Dictionary<string,string?>(MinimumToolEnvironment(executable),StringComparer.OrdinalIgnoreCase);if(environment is not null)foreach(var item in environment)toolEnvironment[item.Key]=item.Value;var operations=new ToolRecipeOperations(state);await operations.Tools.RegisterAsync(new RegisteredToolDefinition(name,1,executable,sha,TimeoutSeconds:3600,ExpectedExitCodes:[0],MaxArguments:256,Environment:toolEnvironment,VersionProbe:new ToolProbeDefinition(["--version"],[0])));
}
static IReadOnlyDictionary<string,string?> MinimumToolEnvironment(string executable)
{
    var values=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);foreach(var key in new[]{"SystemRoot","WINDIR","TEMP","TMP","DOTNET_ROOT"})if(Environment.GetEnvironmentVariable(key) is {Length:>0} value)values[key]=value;if(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable),"dotnet.exe"))values["DOTNET_ROOT"]=Path.GetDirectoryName(Path.GetFullPath(executable));return values;
}
static void SetExplicitEnvironment(ProcessStartInfo start,IReadOnlyDictionary<string,string?> values){start.Environment.Clear();foreach(var item in values)if(item.Value is not null)start.Environment[item.Key]=item.Value;}
static string ResolveOnPath(string name)
{
    if (name == "pwsh.exe" && OperatingSystem.IsWindows()) return PowerShell7ToolBootstrap.ResolveExecutable();
    foreach(var directory in (Environment.GetEnvironmentVariable("PATH")??string.Empty).Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)){var candidate=Path.Combine(directory.Trim('"'),name);if(File.Exists(candidate))return Path.GetFullPath(candidate);}throw new FileNotFoundException($"required test tool not found on PATH: {name}");
}

sealed class TempRoot : IDisposable
{
    public TempRoot() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
    public string Path { get; }
    public string Dir(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}

sealed class ServiceLease(Process service,Process? tray=null) : IDisposable
{
    public int ExitCode=>service.ExitCode;
    public async Task WaitForExitAsync(TimeSpan timeout){using var cancellation=new CancellationTokenSource(timeout);await service.WaitForExitAsync(cancellation.Token);}
    public void Dispose()
    {
        Stop(tray);Stop(service);
    }
    private static void Stop(Process? process){if(process is null)return;if(!process.HasExited)process.Kill(entireProcessTree:true);process.WaitForExit();process.Dispose();}
}

sealed class ProcessLease(Process process):IDisposable
{
    public void Dispose(){if(!process.HasExited)process.Kill(true);process.WaitForExit();process.Dispose();}
}

sealed class CaptureDispatcher : IOperationDispatcher
{
    public OperationRequest? Last{get;private set;}
    public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default){Last=request;return Task.FromResult(new OperationReceipt(true,"fake-jid",request.Rid??"fake-rid","done",request.Op));}
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

sealed class RecordingDispatcher(Func<OperationRequest,OperationReceipt> execute) : IOperationDispatcher
{
    public int Count { get; private set; }
    public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Count++;
        return Task.FromResult(execute(request));
    }
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

sealed class EngineDispatcher(AgentOpsEngine engine) : IOperationDispatcher
{
    public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default)=>engine.ExecuteAsync(request,cancellationToken);
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

sealed class NsisVersionRunner : IToolInvocationRunner
{
    public Task<ProcessResult> RunAsync(ToolInvocation invocation,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProcessResult(0,"v3.12",string.Empty,false,false,false));
    }
}

sealed class BrokerDispatcher(AuthenticatedServiceBroker broker,LocalPeer peer) : IOperationDispatcher
{
    public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default)=>broker.HandleUserAsync(request,peer,cancellationToken);
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

sealed class McpLineReader : TextReader
{
    private readonly Channel<string> _lines=Channel.CreateUnbounded<string>(new UnboundedChannelOptions{SingleReader=true,SingleWriter=true});
    public void Send(string line)=>_lines.Writer.TryWrite(line);
    public void Complete()=>_lines.Writer.TryComplete();
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken){try{return await _lines.Reader.ReadAsync(cancellationToken);}catch(ChannelClosedException){return null;}}
}

sealed class McpCaptureWriter : StringWriter
{
    private readonly TaskCompletionSource<string> _response=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> Response=>_response.Task;
    public override Task WriteLineAsync(string? value){if(value?.Contains("\"id\":2",StringComparison.Ordinal)==true)_response.TrySetResult(value);return base.WriteLineAsync(value);}
}
