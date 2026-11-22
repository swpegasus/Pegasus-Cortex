using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

internal static class CortexProcessorPolicyTests
{
    internal static int Run()
    {
        try
        {
            var saved=CortexProcessorPolicy.Default;
            var state=new CortexProcessorPolicyState(saved,[0,1,2],[1,2],[1,2],true,true,null);
            var controller=new FakeController(settings=>saved=settings);
            using var page=new CortexProcessorPolicyPage(()=>saved,()=>state,controller,_=>{});
            var controls=Descendants(page).ToArray();
            var factory=controls.OfType<CheckBox>().Single();
            var list=controls.OfType<CheckedListBox>().Single();
            var effective=controls.OfType<Label>().Single(item=>item.AccessibleName=="Effective processor state");
            var status=controls.OfType<Label>().Single(item=>item.AccessibleName=="Processor settings status");
            Check(factory.Checked&&!list.GetItemChecked(0)&&list.GetItemChecked(1)&&list.GetItemChecked(2)&&!list.Enabled,
                "factory UI did not leave CPU0 unchecked and other available processors selected");
            Check(effective.Text.Contains("This Host at startup",StringComparison.Ordinal)&&
                effective.Text.Contains("effective set has not been queried",StringComparison.Ordinal),
                "Host snapshot was misrepresented as the service effective state");
            factory.Checked=false;
            list.SetItemChecked(0,true);
            page.SaveSelectionAsync().GetAwaiter().GetResult();
            Check(controller.Saves==1&&saved.AllowedLogicalProcessors.SequenceEqual([0,1,2])&&
                effective.Text.Contains("Restart pending",StringComparison.Ordinal)&&status.Text.Contains("neither was restarted",StringComparison.Ordinal),
                "custom CPU0 save was lost or implied a live apply/restart");
            for(var index=0;index<list.Items.Count;index++)list.SetItemChecked(index,false);
            page.SaveSelectionAsync().GetAwaiter().GetResult();
            Check(controller.Saves==1&&status.Text.Contains("at least one",StringComparison.Ordinal),"empty explicit set reached the controller");
            factory.Checked=true;
            Check(!list.GetItemChecked(0)&&list.GetItemChecked(1)&&list.GetItemChecked(2),"restoring default retained CPU0");
            controller.Refuse=true;
            page.SaveSelectionAsync().GetAwaiter().GetResult();
            Check(controller.Saves==2&&status.Text.Contains("cancelled",StringComparison.Ordinal)&&saved.AllowedLogicalProcessors.Contains(0),
                "cancelled save was shown as saved or mutated the persisted fixture");
            controller.Refuse=false;controller.Throw=true;
            page.SaveSelectionAsync().GetAwaiter().GetResult();
            Check(controller.Saves==3&&status.Text.Contains("not confirmed",StringComparison.Ordinal)&&
                status.Text.Contains("Refresh",StringComparison.Ordinal),"controller exception escaped or invited automatic replay");
            var completion=new TaskCompletionSource<CortexProcessorSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed=new CortexProcessorPolicyPage(()=>CortexProcessorPolicy.Default,()=>state,
                new PendingController(completion.Task),_=>{});
            var context=SynchronizationContext.Current;
            Task pending;
            // The deliberately incomplete save is joined without a WinForms
            // message loop; its disposed continuation must not need that loop.
            try { SynchronizationContext.SetSynchronizationContext(null);pending=disposed.SaveSelectionAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
            disposed.Dispose();
            completion.SetResult(new(true));
            pending.GetAwaiter().GetResult();
            var unavailableSaved=new CortexProcessorSettings(CortexProcessorPolicy.Contract,1,[1,5]);
            using var unavailable=new CortexProcessorPolicyPage(()=>unavailableSaved,
                ()=>state with { Settings=unavailableSaved,Requested=[1,5],Effective=[1],Issue="Some saved processors are unavailable." },
                new FakeController(settings=>unavailableSaved=settings),_=>{});
            var unavailableControls=Descendants(unavailable).ToArray();
            var unavailableList=unavailableControls.OfType<CheckedListBox>().Single();
            Check(unavailableList.Items.Count==4&&unavailableList.GetItemChecked(3)&&
                unavailableList.Items[3].ToString()!.Contains("unavailable",StringComparison.Ordinal),
                "saved unavailable CPU was hidden or unchecked");
            unavailable.SaveSelectionAsync().GetAwaiter().GetResult();
            Check(unavailableSaved.AllowedLogicalProcessors.SequenceEqual([1,5]),"save silently dropped an unavailable saved CPU");
            unavailableControls.OfType<CheckBox>().Single().Checked=true;
            Check(!unavailableList.GetItemChecked(0)&&unavailableList.GetItemChecked(1)&&unavailableList.GetItemChecked(2)&&
                !unavailableList.GetItemChecked(3),"factory default selected CPU0 or an unavailable saved CPU");
            using var unsupported=new CortexProcessorPolicyPage(()=>CortexProcessorPolicy.Default,
                ()=>state with { Supported=false,Applied=false,Available=[],Effective=[],Issue="Multiple processor groups are unsupported." },
                controller,_=>{});
            Check(!Descendants(unsupported).OfType<CheckedListBox>().Single().Enabled&&
                Descendants(unsupported).OfType<Label>().Any(item=>item.Text.Contains("unsupported",StringComparison.OrdinalIgnoreCase)),
                "multi-group degradation looked like an applied truncated selection");
            Check(!page.Visible||page.Parent is null,"fixture unexpectedly showed a window");
            Console.WriteLine("PASS Cortex processor page (injected metadata/controller, unshown controls; no UAC/settings/runtime/affinity action)");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine("FAIL Cortex processor page: "+error);return 1;}
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach(Control child in root.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private sealed class FakeController(Action<CortexProcessorSettings> save):ICortexProcessorPolicyController
    {
        internal int Saves { get; private set; }
        internal bool Refuse { get; set; }
        internal bool Throw { get; set; }
        public Task<CortexProcessorSaveResult> SaveAsync(CortexProcessorSettings settings,CancellationToken cancellationToken)
        {
            Saves++;
            if(Throw)throw new InvalidOperationException("fixture controller failed");
            if(Refuse)return Task.FromResult(new CortexProcessorSaveResult(false,"Windows elevation was cancelled."));
            save(settings);return Task.FromResult(new CortexProcessorSaveResult(true));
        }
    }
    private sealed class PendingController(Task<CortexProcessorSaveResult> result):ICortexProcessorPolicyController
    {
        public Task<CortexProcessorSaveResult> SaveAsync(CortexProcessorSettings settings,CancellationToken cancellationToken)=>result;
    }
}
