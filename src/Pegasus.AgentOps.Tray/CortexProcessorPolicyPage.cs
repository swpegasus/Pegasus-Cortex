using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed class CortexProcessorPolicyPage:UserControl
{
    private readonly Func<CortexProcessorSettings> _read;
    private readonly Func<CortexProcessorPolicyState?> _observe;
    private readonly ICortexProcessorPolicyController _controller;
    private readonly CheckBox _factory=new() { AutoSize=true,Text="Use default: all available processors except CPU0",AccessibleName="Use default processor selection" };
    private readonly CheckedListBox _processors=new() { Width=340,Height=235,CheckOnClick=true,AccessibleName="Allowed logical processors" };
    private readonly Button _save=new() { AutoSize=true,Text="Save processor settings…",AccessibleName="Save processor settings" };
    private readonly Label _effective=new() { AutoSize=true,MaximumSize=new Size(720,0),AccessibleName="Effective processor state" };
    private readonly Label _status=new() { AutoSize=true,MaximumSize=new Size(720,0),AccessibleName="Processor settings status" };
    private CortexProcessorSettings? _saved;
    private int[] _available=[];
    private bool _supported;
    private bool _loading;
    private bool _busy;

    internal CortexProcessorPolicyPage(Func<CortexProcessorSettings>? read=null,
        Func<CortexProcessorPolicyState?>? observe=null,ICortexProcessorPolicyController? controller=null,
        Action<Control>? theme=null)
    {
        _read=read??CortexProcessorPolicyControl.ReadCurrent;
        _observe=observe??(()=>CortexProcessorPolicy.Current??CortexProcessorPolicy.ObserveCurrentProcess());
        _controller=controller??new CortexProcessorPolicyControl();
        var body=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(20) };
        body.Controls.Add(new Label { AutoSize=true,Font=CortexTheme.HeadingFont,Text="Cortex processors" });
        body.Controls.Add(new Label
        {
            AutoSize=true,MaximumSize=new Size(720,0),Text="Choose the logical processors available to the Cortex owner Host and optional administrator service. " +
                "CPU0 is excluded by default; choose a custom selection to include it. Saving requests Windows elevation and applies after each resident next starts."
        });
        body.Controls.Add(_factory);
        body.Controls.Add(_processors);
        var bar=new FlowLayoutPanel { AutoSize=true };
        var refresh=new Button { AutoSize=true,Text="Refresh",AccessibleName="Refresh processor settings" };
        bar.Controls.AddRange([_save,refresh]);
        body.Controls.Add(bar);
        body.Controls.Add(_effective);
        body.Controls.Add(_status);
        body.Controls.Add(new Label
        {
            AutoSize=true,MaximumSize=new Size(720,0),Margin=new Padding(0,16,0,0),
            Text="Direct child processes naturally inherit process affinity. Existing unrelated applications are not changed. " +
                "This does not change process priority or reserve CPU0 for Windows. A single-processor system keeps its usable processor. " +
                "Multiple processor groups are currently unsupported; Cortex keeps the existing Windows processor set on those systems."
        });
        Controls.Add(body);
        _factory.CheckedChanged+=(_,_)=>{if(!_loading)ApplyDefaultSelection();};
        refresh.Click+=(_,_)=>Reload();
        _save.Click+=async(_,_)=>await SaveSelectionAsync();
        Reload();
        (theme??CortexTheme.Apply)(this);
    }

    private void Reload()
    {
        if(_busy)return;
        _loading=true;
        try
        {
            _saved=CortexProcessorPolicy.Validate(_read());
            var state=_observe();
            _supported=state?.Supported==true;
            _available=state?.Available.Distinct().Order().ToArray()??[];
            _processors.Items.Clear();
            foreach(var processor in _available.Union(_saved.AllowedLogicalProcessors).Order())
                _processors.Items.Add(new ProcessorChoice(processor,_available.Contains(processor)));
            _factory.Checked=_saved.AllowedLogicalProcessors.Length==0;
            for(var index=0;index<_processors.Items.Count;index++)
            {
                var processor=((ProcessorChoice)_processors.Items[index]).Id;
                _processors.SetItemChecked(index,_factory.Checked?_available.Contains(processor)&&processor!=0:
                    _saved.AllowedLogicalProcessors.Contains(processor));
            }
            var selection=CortexProcessorPolicy.FormatSelection(_saved);
            var pending=state is null||selection!=CortexProcessorPolicy.FormatSelection(state.Settings);
            _effective.Text="Saved: "+(selection=="default"?"all available processors except CPU0":selection)+".\r\n"+
                (state is null?"This Host's applied processor policy is unavailable.":
                    $"This Host at startup: effective [{(state.Effective.Length==0?"not observed":string.Join(", ",state.Effective))}]; "+
                    (state.Applied?"selection applied.":"selection not applied.")+(state.Issue is null?"":" "+state.Issue))+"\r\n"+
                (pending?"Restart pending for this Host. ":"")+"The administrator service reads the saved selection on its next start; its effective set has not been queried here.";
            _status.Text="Saving does not stop or restart Cortex or the administrator service.";
        }
        catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException)
        {
            _saved=null;_processors.Items.Clear();
            _effective.Text="Saved and effective processor settings could not be loaded.";
            _status.Text="Refresh after resolving the settings error: "+error.GetType().Name;
        }
        finally {_loading=false;UpdateControls();}
    }

    private void ApplyDefaultSelection()
    {
        if(_factory.Checked)
            for(var index=0;index<_processors.Items.Count;index++)
            {
                var processor=(ProcessorChoice)_processors.Items[index];
                _processors.SetItemChecked(index,processor.Available&&processor.Id!=0);
            }
        UpdateControls();
    }

    private void UpdateControls()
    {
        _factory.Enabled=!_busy&&_saved is not null;
        _processors.Enabled=!_busy&&!_factory.Checked&&_supported&&_saved is not null;
        _save.Enabled=!_busy&&_saved is not null&&(_factory.Checked||_supported&&_available.Length>0);
    }

    internal async Task SaveSelectionAsync()
    {
        if(_busy||_saved is null)return;
        var processors=_processors.CheckedItems.Cast<ProcessorChoice>().Select(item=>item.Id).ToArray();
        if(!_factory.Checked&&processors.Length==0){_status.Text="Choose at least one processor, or use the default selection.";return;}
        var settings=_factory.Checked?CortexProcessorPolicy.Default:new CortexProcessorSettings(CortexProcessorPolicy.Contract,1,processors);
        _busy=true;UpdateControls();_status.Text="Waiting for Windows elevation to save processor settings…";
        try
        {
            var result=await _controller.SaveAsync(settings,CancellationToken.None);
            if(IsDisposed)return;
            if(result.Ok)
            {
                _busy=false;Reload();
                _status.Text="Processor settings saved. Restart Cortex and the optional administrator service when convenient to apply them; neither was restarted.";
            }
            else _status.Text=result.Error??"Processor settings were not confirmed.";
        }
        catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException)
        {if(!IsDisposed)_status.Text="Processor settings were not confirmed: "+error.GetType().Name+". Refresh before trying again.";}
        finally {_busy=false;if(!IsDisposed)UpdateControls();}
    }

    private sealed record ProcessorChoice(int Id,bool Available)
    {
        public override string ToString()=>"CPU"+Id+(Available?string.Empty:" (unavailable to this Host)");
    }
}
