namespace Pegasus.AgentOps.Cli;

internal sealed record UnixTranslation(string Op, string[] Tokens);

internal sealed class UnixCompatibilityException(string code, string command, string remedy) : Exception(code)
{
    public string Code { get; } = code;
    public string Command { get; } = command;
    public string Remedy { get; } = remedy;
}

internal static class UnixCompatibilityParser
{
    internal static UnixTranslation Parse(string[] input)
    {
        if (input.Length == 0) throw Fail("invalid", "", "Specify one supported command after pao ux.");
        var command = input[0].ToLowerInvariant(); var arg = input[1..];
        return command switch
        {
            "pwd" when arg.Length == 0 => new("ctx", ["cur"]),
            "ls" => OnePath("ls", StripFlags(command, arg, "-a", "-l", "-la", "-al"), defaultPath: "."),
            "cat" => OnePath("rd", NoFlags(command,arg)),
            "stat" => OnePath("stat", NoFlags(command,arg)),
            "sha256sum" => OnePath("sha", NoFlags(command,arg)),
            "mkdir" => OnePath("mkdir", StripFlags(command,arg,"-p")),
            "cp" => TwoPaths("cp", StripFlags(command,arg,"-r","-R")),
            "mv" => TwoPaths("mv", NoFlags(command,arg)),
            "rm" => Remove(arg),
            "find" => Find(arg),
            "grep" => Grep(arg),
            "head" => Slice("head",arg),
            "tail" => Slice("tail",arg),
            "wc" => Count(arg),
            "sort" => Lines("sort",arg),
            "uniq" => Lines("uniq",arg),
            "du" => Disk("du",arg),
            "df" => Disk("df",arg),
            "diff" => TwoPaths("diff",StripFlags(command,arg,"-u")),
            "realpath" or "basename" or "dirname" => Path(command,arg),
            "netstat" or "ss" => Net(command,arg),
            "ps" => Processes(arg),
            "kill" => Kill(arg),
            "zip" => Zip(arg),
            "unzip" => Unzip(arg),
            _ => throw Fail("unsupported_command",command,"Use a supported pao ux command or a fixed typed pao_* tool; no shell fallback is attempted.")
        };
    }

    private static UnixTranslation Find(string[] input)
    {
        var root=".";string? pattern=null;var type="any";var max=200;var i=0;
        if(i<input.Length&&!input[i].StartsWith('-'))root=input[i++];
        while(i<input.Length){var option=input[i++];if(option=="-name"&&i<input.Length)pattern=input[i++];else if(option=="-type"&&i<input.Length)type=input[i++] switch{"f"=>"file","d"=>"dir",_=>throw Fail("invalid","find","Use -type f or -type d.")};else if(option=="-max"&&i<input.Length&&int.TryParse(input[i++],out max)&&max is>=1 and<=512){}else throw Unsupported("find",option);}
        var values=new List<string>{PathValue(root),$"type={type}",$"max={max}"};if(pattern is not null)values.Add("pattern="+pattern);return new("find",values.ToArray());
    }

    private static UnixTranslation Grep(string[] input)
    {
        var recur=false;var ignore=false;var literal=false;var max=200;string? include=null;var positional=new List<string>();
        for(var i=0;i<input.Length;i++){var token=input[i];if(token is "-R" or "-r")recur=true;else if(token=="-i")ignore=true;else if(token=="-F")literal=true;else if(token is "-n" or "-H"){}else if(token=="-m"&&i+1<input.Length&&int.TryParse(input[++i],out max)&&max is>=1 and<=512){}else if(token.StartsWith("--include=",StringComparison.Ordinal))include=token[10..];else if(token=="--")positional.AddRange(input[(i+1)..]);else if(token.StartsWith('-'))throw Unsupported("grep",token);else positional.Add(token);}
        if(positional.Count is <1 or >2)throw Fail("invalid","grep","Use grep [options] PATTERN [PATH].");
        var values=new List<string>{PathValue(positional.Count==2?positional[1]:"."),"pattern="+positional[0],$"recur={recur}",$"ignore_case={ignore}",$"fmt={(literal?"literal":"regex")}",$"max={max}"};if(include is not null)values.Add("include="+include);return new("search",values.ToArray());
    }

    private static UnixTranslation Slice(string mode,string[] input)
    {
        var count=10;var positional=new List<string>();for(var i=0;i<input.Length;i++){if(input[i]=="-n"&&i+1<input.Length&&int.TryParse(input[++i],out count)&&count is>=1 and<=4096)continue;if(input[i].StartsWith('-'))throw Unsupported(mode,input[i]);positional.Add(input[i]);}
        if(positional.Count!=1)throw Fail("invalid",mode,$"Use {mode} [-n COUNT] PATH.");return new("slice",[PathValue(positional[0]),$"mode={mode}",$"count={count}"]);
    }

    private static UnixTranslation Count(string[] input){var values=StripFlags("wc",input,"-l","-w","-c");return OnePath("count",values);}
    private static UnixTranslation Lines(string op,string[] input){var values=StripFlags(op,input,op=="sort"?"-f":"-i");var translated=OnePath("lines",values);return translated with{Tokens=[..translated.Tokens,$"op={op}",$"ignore_case={input.Contains(op=="sort"?"-f":"-i",StringComparer.Ordinal)}"]};}
    private static UnixTranslation Disk(string op,string[] input){var values=StripFlags(op,input,op=="du"?"-s":"-h",op=="du"?"-h":"-H",op=="du"?"-sh":"--human-readable");var translated=OnePath("disk",values,op=="df"?".":null);return translated with{Tokens=[..translated.Tokens,$"op={op}"]};}
    private static UnixTranslation Path(string command,string[] input){var translated=OnePath("path",NoFlags(command,input));return translated with{Tokens=[..translated.Tokens,$"op={command}"]};}
    private static UnixTranslation Net(string command,string[] input){_ = StripFlags(command,input,"-a","-n","-t","-u","-l","-p","-tunlp");return new("net",["list"]);}
    private static UnixTranslation Processes(string[] input){foreach(var value in input)if(value is not("-e" or "-f" or "-ef" or "-fe" or "aux" or "-a" or "-x"))throw Unsupported("ps",value);return new("proc",["ls","max=256"]);}
    private static UnixTranslation Kill(string[] input){if(input.Length!=2||!int.TryParse(input[0],out var pid)||pid<1)throw Fail("invalid","kill","Use kill PID START with the exact start identity returned by pao ux ps; PID-only or name-only termination is refused.");return new("proc",["stop",pid.ToString(System.Globalization.CultureInfo.InvariantCulture),input[1]]);}
    private static UnixTranslation Remove(string[] input){var values=StripFlags("rm",input,"-r","-R","-f","-rf","-fr");return OnePath("rm",values);}
    private static UnixTranslation Zip(string[] input){var values=StripFlags("zip",input,"-r");if(values.Length!=2)throw Fail("invalid","zip","Use zip [-r] ARCHIVE SOURCE.");return new("zip",["create",PathValue(values[1]),PathValue(values[0])]);}
    private static UnixTranslation Unzip(string[] input){if(input.Length==1)return new("zip",["list",PathValue(input[0])]);if(input.Length==3&&input[1]=="-d")return new("zip",["extract",PathValue(input[0]),PathValue(input[2])]);throw Fail("invalid","unzip","Use unzip ARCHIVE to list or unzip ARCHIVE -d DESTINATION to extract.");}
    private static UnixTranslation OnePath(string op,string[] values,string? defaultPath=null){if(values.Length==0&&defaultPath is not null)values=[defaultPath];if(values.Length!=1)throw Fail("invalid",op,$"Use {op} PATH.");return new(op,[PathValue(values[0])]);}
    private static UnixTranslation TwoPaths(string op,string[] values){if(values.Length!=2)throw Fail("invalid",op,$"Use {op} SOURCE DESTINATION.");return new(op,[PathValue(values[0]),PathValue(values[1])]);}
    private static string[] NoFlags(string command,string[] values){var flag=values.FirstOrDefault(x=>x.StartsWith('-'));if(flag is not null)throw Unsupported(command,flag);return values;}
    private static string[] StripFlags(string command,string[] values,params string[] supported){var result=new List<string>();foreach(var value in values){if(value.StartsWith('-')){if(!supported.Contains(value,StringComparer.Ordinal))throw Unsupported(command,value);}else result.Add(value);}return result.ToArray();}
    private static UnixCompatibilityException Unsupported(string command,string option)=>Fail("unsupported_option",command,$"The {option} option is not supported for pao ux {command}; use the documented typed subset.");
    private static UnixCompatibilityException Fail(string code,string command,string remedy)=>new(code,command,remedy);
    private static string PathValue(string value)
    {
        if(value.StartsWith("//",StringComparison.Ordinal)&&value.Length>2)return "\\\\"+value[2..].Replace('/','\\');
        if(value.Length>0&&value[0]=='/')
        {
            if(value.Length>=3&&char.IsAsciiLetter(value[1])&&value[2]=='/')return char.ToUpperInvariant(value[1])+":"+value[2..].Replace('/','\\');
            throw Fail("path_style","path","Use a Windows path, workspace-relative path, alias-qualified path, or explicit /c/... drive form.");
        }
        return value.Replace('/',System.IO.Path.DirectorySeparatorChar);
    }
}
