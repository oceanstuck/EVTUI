using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using ImageMagick;

using AtlusScriptLibrary.Common.Text.Encodings;

using AtlusScriptLibrary.Common.Libraries;
using AtlusScriptLibrary.Common.Logging;
using AtlusScriptLibrary.Common.Text;

using AtlusScriptLibrary.MessageScriptLanguage;
using AtlusScriptLibrary.MessageScriptLanguage.BinaryModel;
using AtlusScriptLibrary.MessageScriptLanguage.Decompiler;
using AtlusScriptLibrary.MessageScriptLanguage.Compiler;

using AtlusScriptLibrary.FlowScriptLanguage;
using AtlusScriptLibrary.FlowScriptLanguage.BinaryModel;
using AtlusScriptLibrary.FlowScriptLanguage.Decompiler;
using AtlusScriptLibrary.FlowScriptLanguage.Compiler;

using MsgFormatVersion = AtlusScriptLibrary.MessageScriptLanguage.FormatVersion;
using FlowFormatVersion = AtlusScriptLibrary.FlowScriptLanguage.FormatVersion;
using ASTLogEventArgs = AtlusScriptLibrary.Common.Logging.LogEventArgs;

namespace EVTUI;

public struct AudioCues
{
    public AudioCues()
    {
        EnCues = new LocaleCues();
        JpCues = new LocaleCues();
    }

    public LocaleCues EnCues { get; set; }
    public LocaleCues JpCues { get; set; }
}

public struct LocaleCues
{
    public LocaleCues()
    {
        EventVoice = new Dictionary<uint, MessageCue>();
        EventSFX   = new Dictionary<uint, MessageCue>();
        Common     = new Dictionary<uint, MessageCue>();
        Field      = new Dictionary<uint, MessageCue>();
    }

    public Dictionary<uint, MessageCue> EventVoice { get; set; }
    public Dictionary<uint, MessageCue> EventSFX   { get; set; }
    public Dictionary<uint, MessageCue> Common     { get; set; }
    public Dictionary<uint, MessageCue> Field      { get; set; }
}

public class MessageCue
{
    public MessageCue(string speakerName, string turnName, int indWithinTurn)
    {
        SpeakerName   = speakerName;
        TurnName      = turnName;
        IndWithinTurn = indWithinTurn;
    }

    public string                   SpeakerName   { get; set; }
    public string                   TurnName      { get; set; }
    public int                      IndWithinTurn { get; set; }
    public (uint Lower, uint Upper) CueRange      { get; set; }

    public string Stringification { get { return $"{SpeakerName} - {TurnName} ({IndWithinTurn})"; } }
}

public class AppLogListener : LogListener
{
    public string Text;

    // LogLevel.All includes LogLevel.Trace which gets soooooo slow for longer scripts
    public AppLogListener() : base(LogLevel.Debug | LogLevel.Info | LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)
    {
        this.Text = "";
    }

    protected override void OnLogCore( object sender, ASTLogEventArgs e )
    {
        if (this.Text.Length > 0)
            this.Text += "\n";
        this.Text += $"{DateTime.Now} {e.ChannelName} {e.Level}: {e.Message}";
    }
}


public class ScriptManager
{
    protected DataManager config;

    /////////////////////////////
    // *** PRIVATE MEMBERS *** //
    /////////////////////////////
    private string GameName;
    private Dictionary<(byte, byte), string> EnCharLookup;
    private Dictionary<(byte, byte), string> JpCharLookup;
    private AtlusEncoding Encoding => GetEncodingFromLang(config.Language);
    private static Dictionary<string, string> CpkLanguages = new()
    {
        { "EN.CPK", "English" },
        { "JP.CPK", "Japanese" },
        { "BASE.CPK", "Japanese" },
        { "FR.CPK", "French" },
        { "IT.CPK", "Italian" },
        { "DE.CPK", "German" },
        { "ES.CPK", "Spanish" },
        { "KR.CPK", "Korean" },
        { "SC.CPK", "Simplified Chinese" },
        { "TC.CPK", "Traditional Chinese" }
    };
    private static Dictionary<string, string> LangCodeToP5Encoding { get; } = new()
    {
        { "en", "P5" },
        { "ja", "P5" },
        { "fr", "P5" },
        { "it", "P5" },
        { "de", "P5" },
        { "es", "P5" },
        { "ko", "P5_Korean" },
        { "zh-Hans", "P5_Chinese" },
        { "zh-Hant", "P5R_Chinese" }
    };
    private static Dictionary<string, string> LangCodeToP5REncoding { get; } = new()
    {
        { "en", "P5R_EFIGS" },
        { "ja", "P5R_Japanese" },
        { "fr", "P5R_EFIGS" },
        { "it", "P5R_EFIGS" },
        { "de", "P5R_EFIGS" },
        { "es", "P5R_EFIGS" },
        { "ko", "P5_Korean" },
        { "zh-Hans", "P5R_CHS" },
        { "zh-Hant", "P5R_CHT" }
    };

    ////////////////////////////
    // *** PUBLIC MEMBERS *** //
    ////////////////////////////
    public string?                                        ActiveBMD = null;
    public string? ActiveVanillaBMD = null;
    public Dictionary<string, BMD>                        BMDFiles { get; }

    public string?                                        ActiveBF = null;
    public string? ActiveVanillaBF = null;
    //public Dictionary<string, BF>                         BFFiles { get; }

    //public Dictionary<string, List<string>> vanillaScriptList { get; }
    //public Dictionary<string, Dictionary<string, Dictionary<string, string>>> vanillaScriptTexts { get; }

    //public Dictionary<string, List<string>>                                   ScriptList  { get; }
    //public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ScriptTexts { get; }
    //public Dictionary<string, Dictionary<string, string>>                     ScriptErrors   { get; }
    //public Dictionary<string, Dictionary<string, string>> VanillaScriptErrors { get; }

    //public Dictionary<string, Dictionary<string, Dictionary<string, string>>> EmulatedScriptPaths { get; }
    //public Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, string>>>> LocalizedScriptPaths { get; }
    //public Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, string>>>>> LocalizedScriptTexts { get; }
    public static Dictionary<string, string> LangCodeDict { get; } = new()
    {
        { "English", "en" },
        { "Japanese", "ja" },
        { "French", "fr" },
        { "Italian", "it" },
        { "German", "de" },
        { "Spanish", "es" },
        { "Korean", "ko" },
        { "Simplified Chinese", "zh-Hans" },
        { "Traditional Chinese", "zh-Hant" }
    };

    public record ScriptInfo(string path, string scriptKind)
    {
        public string cpk => path.Substring(0, path.IndexOfAny(['\\', '/']));
        public string pathInCpk => path.Substring(path.IndexOfAny(['\\', '/']) + 1);

        public string? flowPath;
        public string? msgPath;

        public string? log;

        // additional script files imported by the main .flow
        //public List<string> additionalFlowPaths;
        //public List<string> additionalMsgPaths;
    }
    public record VanillaScriptInfo(string path, string scriptKind) : ScriptInfo(path, scriptKind)
    {
        public string language => CpkToLang(cpk); // TODO: this probably wont work with console...
    }
    public record ModdedScriptInfo(string path, string scriptKind, bool isEmulated) : ScriptInfo(path, scriptKind)
    {
        public Dictionary<string, string> localizedFlowPaths; // can't think of a lot of cases where one would do this but eh
        public Dictionary<string, string> localizedMsgPaths;
    }

    public List<VanillaScriptInfo> VanillaScripts { get; }
    public List<ModdedScriptInfo> ModdedScripts { get; }
    public Dictionary<string, string?> fileTexts { get; }

    // TODO: this definitely belongs... somewhere else. probably in the actual AudioPreview ViewModel
    public AudioCues EventCues
    {
        get
        {
            AudioCues eventCues = new AudioCues();
            foreach (string key in this.BMDFiles.Keys)
            {
                bool isJP = key.Contains("JP.CPK") || key.Contains("BASE.CPK");
                LocaleCues locale;
                if (isJP)
                    locale = eventCues.JpCues;
                else
                    locale = eventCues.EnCues;
                try
                {
                foreach (Turn turn in this.BMDFiles[key].Turns)
                    for (int indWithinTurn=0; indWithinTurn<turn.Elems.Length; indWithinTurn++)
                        foreach (Node node in this.Parse(turn.Elems[indWithinTurn], isJP))
                        {
                            try
                            {
                                string turnName = this.Parse(turn.Name, isJP)[0].Text;
                                string speakerName = "";
                                if (turn.SpeakerId == 0xFFFF)
                                {
                                    if (turnName.StartsWith("MND_"))
                                        speakerName = "(JOKER)";
                                    else
                                        speakerName = "(SYSTEM)";
                                }
                                else
                                    speakerName = this.Parse(this.BMDFiles[key].Speakers[turn.SpeakerId], isJP)[0].Text;
                                if (node.FunctionTableIndex == 3 && node.FunctionIndex == 1 && node.FunctionArguments[3] != 0)
                                {
                                    // apparently this is doing something but i don't understand how...
                                    // this should really all be in the ViewModel and not the Model, anyway
                                    // TODO
                                    Dictionary<uint, MessageCue> messageCues = new Dictionary<uint, MessageCue>();
                                    switch (node.FunctionArguments[1])
                                    {
                                        case 0:
                                            if (speakerName.StartsWith("(SE)"))
                                                messageCues = locale.EventSFX;
                                            else
                                                messageCues = locale.EventVoice;
                                            break;
                                        case 1:
                                            messageCues = locale.Field;
                                            break;
                                        case 2:
                                            messageCues = locale.Common;
                                            break;
                                        default:
                                            break;
                                    }
                                    messageCues[node.FunctionArguments[3]] = new MessageCue(speakerName, turnName, indWithinTurn+1);
                                }
                            } catch (Exception ex) { Trace.TraceError(ex.ToString()); }
                        }
                // seems like sometimes the BMDFile can be null... or the Turns can be null? unclear
                } catch (Exception ex) { Trace.TraceError(ex.ToString()); }
            }
            return eventCues;
        }
    }

    public List<string> TurnNames
    {
        get
        {
            List<string> names = new List<string>();
            if (!(this.ActiveBMD is null))
                foreach (Turn turn in this.BMDFiles[this.ActiveBMD].Turns)
                {
                    string name = "";
                    foreach (Node node in this.Parse(turn.Name, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                        name += node.Text;
                }
            return names;
        }
    }

    public List<string> MsgNames
    {
        get
        {
            List<string> names = new List<string>();
            if (!(this.ActiveBMD is null))
                for (int i=0; i<this.BMDFiles[this.ActiveBMD].TurnCount; i++)
                {
                    string name = "";
                    foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[i].Name, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                        name += node.Text;
                    if (this.BMDFiles[this.ActiveBMD].TurnKinds[i] == 0)
                        names.Add(name);
                }
            return names;
        }
    }

    public List<string> SelNames
    {
        get
        {
            List<string> names = new List<string>();
            if (!(this.ActiveBMD is null))
                for (int i=0; i<this.BMDFiles[this.ActiveBMD].TurnCount; i++)
                {
                    string name = "";
                    foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[i].Name, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                        name += node.Text;
                    if (this.BMDFiles[this.ActiveBMD].TurnKinds[i] != 0)
                        names.Add(name);
                }
            return names;
        }
    }

    public List<string> SpeakerNames
    {
        get
        {
            List<string> names = new List<string>();
            if (!(this.ActiveBMD is null))
            {
                foreach (byte[] speaker in this.BMDFiles[this.ActiveBMD].Speakers)
                {
                    string name = "";
                    foreach (Node node in this.Parse(speaker, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                        name += node.Text;
                    names.Add(name);
                }
                names.Add("(UNNAMED)");
            }
            return names;
        }
    }

    ////////////////////////////
    // *** PUBLIC METHODS *** //
    ////////////////////////////
    public ScriptManager(DataManager config)
    {
        this.config = config;

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        VanillaScripts = new List<VanillaScriptInfo>();
        ModdedScripts = new List<ModdedScriptInfo>();
        fileTexts = new Dictionary<string, string>();

        //this.ScriptErrors = new Dictionary<string, Dictionary<string, string>>();
        //VanillaScriptErrors = new();

        foreach (string scriptType in new[] { "BMD", "BF" })
        {
            if (scriptType == "BMD")
                this.BMDFiles = new Dictionary<string, BMD>();
            // TODO: actually make BF Serializable :')
            //else
            //    this.BFFiles = new Dictionary<string, BF>();

            //this.ScriptErrors[scriptType] = new Dictionary<string, string>();
            //VanillaScriptErrors[scriptType] = new();
        }
    }

    public void Dispose()
    {
        VanillaScripts.Clear();
        ModdedScripts.Clear();
        fileTexts.Clear();

        //this.ScriptErrors.Clear();
        //VanillaScriptErrors.Clear();
        this.config = null;
    }

    public string RemovePrefix(string prefix, string s)
    {
        return s.Substring((prefix.Length+1), s.Length-(prefix.Length+1));
    }

    public string BasePath(string parentDir, string fileBase)
    {
        return Path.Combine(parentDir, Path.GetDirectoryName(fileBase), Path.GetFileNameWithoutExtension(fileBase));
    }

    public static string GetLangCodeFromLang(string language) => LangCodeDict[language];
    public static string CpkToLang(string cpk) => CpkLanguages[cpk];

    // TODO: this wont work for chinese or korean and requires submodule update to fix
    public AtlusEncoding GetEncodingFromLang(string language) => GetEncodingFromLangCode(GetLangCodeFromLang(language));
    private AtlusEncoding GetEncodingFromLangCode(string langCode)
    {
        bool p5r = config.ProjectManager.ActiveGame.Type.StartsWith("P5R");
        try
        {
            var charsetName = p5r ? LangCodeToP5REncoding[langCode] : LangCodeToP5Encoding[langCode];
            //return AtlusEncoding.Create(charsetName);
            return AtlusEncoding.GetByName(charsetName);
        }
        catch (Exception e)
        {
            return p5r ? AtlusEncoding.Persona5RoyalEFIGS : AtlusEncoding.Persona5;
        }
    }

    /*public void SaveScript(string scriptType, string workingDir, string modDir, string emuDir)
    {
        if (emuDir is not null && !Directory.Exists(Path.Combine(emuDir, scriptType)))
            Directory.CreateDirectory(Path.Combine(emuDir, scriptType));
        foreach (string script in this.ScriptList[scriptType])
        {
            // for now, only saves the active script...
            // there's probably a better way to do this, but i think the UI needs to be a bit clearer
            // for now, this at least is better than the hardcoding there was before, but TODO
            if ((scriptType == "BMD" && script == this.ActiveBMD) || (scriptType == "BF" && script == this.ActiveBF))
            {
                if (!Directory.Exists(Path.GetDirectoryName(Path.Combine(modDir, script))))
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(modDir, script)));

                bool isEmulated = EmulatedScriptPaths[scriptType].ContainsKey(script);
                if (isEmulated)
                {
                    File.Create(Path.Combine(modDir, script)).Dispose(); // create dummy file
                    foreach (var ext in new[] { ".flow", ".msg" })
                    {
                        if (ScriptTexts[scriptType][script].ContainsKey(ext))
                        {
                            var scriptPath = EmulatedScriptPaths[scriptType][script][ext];
                            if (!Directory.Exists(Path.GetDirectoryName(scriptPath)))
                                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                            File.WriteAllText(scriptPath, ScriptTexts[scriptType][script][ext]);
                        }
                    }
                }
                else
                {
                    if (scriptType == "BF")
                        CompileScript(modDir, script);
                    else
                        CompileMessage(modDir, script);
                }

                if (emuDir is null)
                    File.Copy(Path.Combine(workingDir, script), Path.Combine(modDir, script), true);
                else
                {
                    File.Create(Path.Combine(modDir, script)).Dispose();
                    foreach (string ext in new[] { ".flow", ".msg" })
                        if (this.ScriptTexts[scriptType][script].ContainsKey(ext))
                        {
                            // if they already have full path femu, fine, just use that
                            if (File.Exists(this.BasePath(Path.Combine(emuDir, scriptType), script)+ext))
                                File.Copy(this.BasePath(workingDir, script)+ext, this.BasePath(Path.Combine(emuDir, scriptType), script)+ext, true);
                            // otherwise, do it the dummy + top-level (recommended) way
                            else
                            {
                                File.WriteAllText(this.BasePath(workingDir, script)+ext, this.ScriptTexts[scriptType][script][ext]);
                                File.Copy(this.BasePath(workingDir, script)+ext, Path.Combine(emuDir, scriptType, Path.GetFileNameWithoutExtension(script)+ext), true);
                            }
                        }
                }
            }
        }
    }*/

    public void ExportScripts(Func<ModdedScriptInfo, bool> predicate)
    {
        foreach (var script in ModdedScripts.Where(predicate))
            ExportScript(script);
    }
    public void ExportScript(string fileRelativePath) => ExportScript(ModdedScripts.First(s => s.path == fileRelativePath));
    public void ExportScript(ModdedScriptInfo script)
    {
        if (script.msgPath is not null && fileTexts[script.msgPath] is not null && fileTexts[script.msgPath] != string.Empty)
            File.WriteAllText(script.msgPath, fileTexts[script.msgPath]);
        if (script.flowPath is not null && fileTexts[script.flowPath] is not null && fileTexts[script.flowPath] != string.Empty)
            File.WriteAllText(script.flowPath, fileTexts[script.flowPath]);

        if (script.localizedFlowPaths is not null)
        {
            foreach (var path in script.localizedFlowPaths.Values)
                if (fileTexts[path] != string.Empty)
                    File.WriteAllText(path, fileTexts[path]);
        }
        if (script.localizedMsgPaths is not null)
        {
            foreach (var path in script.localizedMsgPaths.Values)
                if (fileTexts[path] != string.Empty) // there are legitimate use cases for having an empty localized .msg (eg. translations for untranslated scrapped events) but imo theyre infrequent enough that it's fine having to make them manually rather than potentially writing an empty file by mistake if you misclick the language dropdown
                    File.WriteAllText(path, fileTexts[path]);
        }

        if (script.isEmulated)
        {
            string dummyFilePath = Path.Combine(config.ProjectManager.ModdedFileDir, script.path);
            if (!File.Exists(dummyFilePath))
                File.Create(dummyFilePath).Dispose();
        }
        else
        {
            // TODO: error reporting if compilation fails
            var log = string.Empty;
            if (script.scriptKind == "BF")
            {
                var success = TryCompileBF(script.flowPath, out var flowScript, out log);
                if (success)
                    flowScript.ToFile(Path.Combine(config.ProjectManager.ModdedFileDir, script.path));
                if (script.localizedFlowPaths is not null)
                    foreach (var (langcode, flow) in script.localizedFlowPaths)
                    {
                        success = TryCompileBF(flow, GetEncodingFromLangCode(langcode), out flowScript, out log);
                        if (success)
                            flowScript.ToFile(Path.ChangeExtension(flow, ".bf"));
                    }
            }
            else
            {
                var success = TryCompileBMD(script.msgPath, out var msgScript, out log);
                if (!success)
                    return;
                msgScript.ToFile(Path.Combine(config.ProjectManager.ModdedFileDir, script.path));
                if (script.localizedMsgPaths is not null)
                    foreach (var (langcode, msg) in script.localizedMsgPaths)
                    {
                        success = TryCompileBMD(msg, GetEncodingFromLangCode(langcode), out msgScript, out log);
                        if (success)
                            msgScript.ToFile(Path.ChangeExtension(msg, ".bmd"));
                    }
            }
        }
    }

    public bool TryCompileBF(ModdedScriptInfo bf, out FlowScript script, out string log) => TryCompileBF(bf, config.Language, out script, out log);
    public bool TryCompileBF(ModdedScriptInfo bf, string language, out FlowScript script, out string log)
    {
        bool success;
        if (bf.isEmulated)
        {
            var vanillaBf = FindMatchingVanillaScript(bf, language);
            var vanillaBfPath = vanillaBf is null ? null : Path.Combine(config.VanillaExtractionPath, vanillaBf.path);
            success = TryCompileEmulatedBF(vanillaBfPath, GetLocalizedFilePath(bf, ".flow", language), new() { GetLocalizedFilePath(bf, ".msg", language) }, GetEncodingFromLang(language), out script, out log);
        }
        else
            success = TryCompileBF(bf.flowPath, GetEncodingFromLang(language), out script, out log);

        if (!success) bf.log = log;
        return success;
    }

    public bool TryCompileBF(string flowPath, out FlowScript script, out string log) => TryCompileBF(flowPath, this.Encoding, out script, out log);
    public bool TryCompileBF(string flowPath, AtlusEncoding encoding, out FlowScript script, out string log)
    {
        var listener = new AppLogListener();
        bool success;
        script = null;

        try
        {
            var compiler = new FlowScriptCompiler(FlowFormatVersion.Version3BigEndian);
            compiler.Encoding = encoding;
            compiler.Library = LibraryLookup.GetLibrary(GameName);
            compiler.EnableProcedureTracing = false;
            compiler.AddListener(listener);

            success = compiler.TryCompile(fileTexts[flowPath], out script);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
            success = false;
        }
        log = listener.Text;
        return success;
    }

    public bool TryCompileBMD(ModdedScriptInfo bmd, out MessageScript script, out string log) => TryCompileBMD(bmd, config.Language, out script, out log);
    public bool TryCompileBMD(ModdedScriptInfo bmd, string language, out MessageScript script, out string log)
    {
        bool success;
        if (bmd.isEmulated)
        {
            var vanillaBmd = FindMatchingVanillaScript(bmd, language);
            var vanillaBmdPath = vanillaBmd is null ? null : Path.Combine(config.VanillaExtractionPath, vanillaBmd.path);
            success = TryCompileEmulatedBMD(vanillaBmdPath, new() { GetLocalizedFilePath(bmd, ".msg", language) }, GetEncodingFromLang(language), out script, out log);
        }
        else
            success = TryCompileBMD(bmd.msgPath, GetEncodingFromLang(language), out script, out log);

        if (!success) bmd.log = log;
        return success;
    }

    public bool TryCompileBMD(string msgPath, out MessageScript script, out string log) => TryCompileBMD(msgPath, this.Encoding, out script, out log);
    public bool TryCompileBMD(string msgPath, AtlusEncoding encoding, out MessageScript script, out string log)
    {
        var listener = new AppLogListener();
        bool success;
        script = null;

        try
        {
            var compiler = new MessageScriptCompiler(MsgFormatVersion.Version1BigEndian, encoding);
            compiler.Library = LibraryLookup.GetLibrary(GameName);
            compiler.AddListener(listener);

            success = compiler.TryCompile(fileTexts[msgPath], out script);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
            success = false;
        }

        log = listener.Text;
        return success;
    }

    public bool TryCompileEmulatedBF(string baseBf, string flowImport, List<string> importPaths, out FlowScript script, out string log) => TryCompileEmulatedBF(baseBf, flowImport, importPaths, this.Encoding, out script, out log);
    public bool TryCompileEmulatedBF(string baseBf, string flowImport, List<string> importPaths, AtlusEncoding encoding, out FlowScript script, out string log)
    {
        // importPaths should contain .msg import *if* flow import does not exist, otherwise importPaths should be empty/dependency mod imports only and flowImport should just import the .msg file directly
        // ...is how it works in actual femu, but bc we can't actually emu localized files here we instead stick localized .msg in importPaths and pray
        var listener = new AppLogListener();
        bool success;
        script = null;

        try
        {
            var compiler = new FlowScriptCompiler(FlowFormatVersion.Version3BigEndian);
            compiler.Encoding = encoding;
            compiler.Library = LibraryLookup.GetLibrary(GameName);
            compiler.EnableProcedureTracing = false;
            compiler.OverwriteExistingMsgs = true;
            // compiler.OverwriteExistingProcedures = true; // this feature only exists on upstream
            compiler.ProcedureHookMode = ProcedureHookMode.ImportedOnly;
            compiler.AddListener(listener);

            FileStream baseBfStream = File.Exists(baseBf) ? new FileStream(baseBf, FileMode.Open) : null;
            success = compiler.TryCompileWithImports(baseBfStream, importPaths, flowImport, out script);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
            success = false;
        }
        log = listener.Text;
        return success;
    }

    public bool TryCompileEmulatedBMD(string baseBmd, List<string> importPaths, out MessageScript script, out string log) => TryCompileEmulatedBMD(baseBmd, importPaths, this.Encoding, out script, out log);
    public bool TryCompileEmulatedBMD(string baseBmd, List<string> importPaths, AtlusEncoding encoding, out MessageScript script, out string log)
    {
        var listener = new AppLogListener();
        bool success;
        script = null;

        try
        {
            var compiler = new MessageScriptCompiler(MsgFormatVersion.Version1BigEndian, encoding);
            compiler.Library = LibraryLookup.GetLibrary(GameName);
            compiler.AddListener(listener);
            compiler.OverwriteExistingMsgs = true;

            FileStream baseBmdStream = File.Exists(baseBmd) ? new FileStream(baseBmd, FileMode.Open) : null;
            success = compiler.TryCompileWithImports(baseBmdStream, importPaths, out script);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
            success = false;
        }
        log = listener.Text;
        return success;
    }

    /*public void FindEmulatorImports(string modDir, string emuDir, List<string> dummyFiles, string fileKind)
    {
        var importPathBase = Path.Combine(emuDir, fileKind);
        var importPathLen = importPathBase.Length;
        foreach (var dummyFile in dummyFiles)
        {
            var key = Path.GetRelativePath(modDir, dummyFile);
            ScriptList[fileKind].Add(key);
            EmulatedScriptPaths[fileKind][key] = new Dictionary<string, string>();
            var parts = key.Split(Path.DirectorySeparatorChar);
            parts[parts.Length - 1] = Path.ChangeExtension(parts[parts.Length - 1], ".msg");

            var defaultMsgImportPath = Path.Combine(importPathBase, parts[parts.Length - 1]);
            var msgImportPath = defaultMsgImportPath;
            string flowImportPath = Path.ChangeExtension(msgImportPath, ".flow");
            var defaultFlowImportPath = flowImportPath;
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                if (File.Exists(msgImportPath))
                {
                    EmulatedScriptPaths[fileKind][key][".msg"] = msgImportPath;
                    ScriptTexts[fileKind][key][".msg"] = File.ReadAllText(msgImportPath);
                }
                if (fileKind == "BF" && File.Exists(flowImportPath))
                {
                    EmulatedScriptPaths[fileKind][key][".flow"] = flowImportPath;
                    ScriptTexts[fileKind][key][".flow"] = File.ReadAllText(flowImportPath);
                }

                if (EmulatedScriptPaths[fileKind][key].Count > 0) break; // this line assumes flow and msg paths will always be in the same folder, which is *usually* true but keep in mind if trying to add logic for manually imported files
                msgImportPath.Insert(importPathLen, parts[i]);
                flowImportPath.Insert(importPathLen, parts[i]);
            }

            // if couldn't find imports, then use default path and treat imports as empty
            if (!EmulatedScriptPaths[fileKind][key].ContainsKey(".msg"))
            {
                EmulatedScriptPaths[fileKind][key][".msg"] = defaultMsgImportPath;
                ScriptTexts[fileKind][key][".msg"] = String.Empty;
            }
            if (fileKind == "BF" && !EmulatedScriptPaths[fileKind][key].ContainsKey(".flow"))
            {
                EmulatedScriptPaths[fileKind][key][".flow"] = defaultFlowImportPath;
                ScriptTexts[fileKind][key][".flow"] = String.Empty;
            }
        }
    }*/

    public Dictionary<string, string> FindLocalizedFiles(string englishFileRoute)
    {
        var localizedFileDict = new Dictionary<string, string>();
        foreach (var (lang, langCode) in LangCodeDict)
        {
            var curLangFolder = Path.Combine(config.ProjectManager.LocalizedFileDir, langCode);
            if (!Directory.Exists(curLangFolder)) continue;
            var files = Directory.EnumerateFiles(curLangFolder, "*.*", SearchOption.AllDirectories).ToList();
            foreach (var file in files)
            {
                if (englishFileRoute.EndsWith(Path.GetRelativePath(curLangFolder, file), StringComparison.InvariantCultureIgnoreCase))
                {
                    localizedFileDict[langCode] = file;
                    fileTexts[file] = File.ReadAllText(file);
                    break;
                }
            }
        }
        return localizedFileDict;
    }

    public string GetLocalizedFilePath(ModdedScriptInfo scriptInfo, string ext, string language)
    {
        string filePath;
        var langCode = GetLangCodeFromLang(language);
        if (ext == ".flow")
        {
            if (language != "English" && scriptInfo.localizedFlowPaths is not null && scriptInfo.localizedFlowPaths.ContainsKey(langCode))
                filePath = scriptInfo.localizedFlowPaths[langCode];
            else
                filePath = scriptInfo.flowPath;
        }
        else
        {
            if (language != "English" && scriptInfo.localizedMsgPaths is not null && scriptInfo.localizedMsgPaths.ContainsKey(langCode))
            {
                /* TODO: this was intended to make the process of creating new imports for a language possible but auto-creating file info isnt necessarily desirable here
                 * might be better as a prompt upon selecting a language for which no file exists (which also prevents creating stuff on misclick)
                if (!scriptInfo.localizedMsgPaths.ContainsKey(langCode)) // this should only happen if the file doesnt exist
                {
                    scriptInfo.localizedMsgPaths[langCode] = Path.Combine(config.ProjectManager.LocalizedFileDir, langCode, Path.GetFileName(scriptInfo.msgPath));
                    var newScriptText = string.Empty;
                    if (!scriptInfo.isEmulated)
                    {
                        var vanillaScript = VanillaScripts.FirstOrDefault(s => s.pathInCpk == scriptInfo.pathInCpk && s.language == language, null);
                        if (vanillaScript != null)
                            newScriptText = fileTexts[vanillaScript.msgPath];

                    }
                    fileTexts[scriptInfo.localizedMsgPaths[langCode]] = newScriptText;
                }*/
                filePath = scriptInfo.localizedMsgPaths[langCode];
            }
            else
                filePath = scriptInfo.msgPath;
        }

        return filePath;
    }

    public void FetchScripts()
    {
        // TODO: I gooootta handle game/locale/encoding more elegantly, lmao
        if (config.ProjectManager.ActiveGame.Type.StartsWith("P5R"))
        {
            this.GameName = "p5r";
            this.EnCharLookup = CharLookup("P5R_EFIGS");
            this.JpCharLookup = CharLookup("P5R_Japanese");
        }
        else
        {
            this.GameName = "p5";
            this.EnCharLookup = CharLookup("P5");
            this.JpCharLookup = this.EnCharLookup;
        }

        foreach (var vanillaBf in config.EventManager.VanillaBfPaths)
        {
            VanillaScriptInfo scriptInfo = new(Path.GetRelativePath(config.VanillaExtractionPath, vanillaBf), "BF");
            scriptInfo.flowPath = Path.ChangeExtension(vanillaBf, ".flow");
            scriptInfo.msgPath = Path.ChangeExtension(vanillaBf, ".msg");

            string error = string.Empty;
            string flowText = string.Empty;
            string? msgText = null;

            if (File.Exists(scriptInfo.flowPath) || TryDecompileBF(vanillaBf, GetEncodingFromLang(scriptInfo.language), scriptInfo.flowPath, out error))
            {
                flowText = File.ReadAllText(scriptInfo.flowPath);
                if (File.Exists(scriptInfo.msgPath)) { msgText = File.ReadAllText(scriptInfo.msgPath); }
                fileTexts[scriptInfo.flowPath] = flowText;
                fileTexts[scriptInfo.msgPath] = msgText;

                VanillaScripts.Add(scriptInfo);
            }
            scriptInfo.log = error;
        }

        foreach (var vanillaBmd in config.EventManager.VanillaBmdPaths)
        {
            VanillaScriptInfo scriptInfo = new(Path.GetRelativePath(config.VanillaExtractionPath, vanillaBmd), "BMD");
            scriptInfo.msgPath = Path.ChangeExtension(vanillaBmd, ".msg");
            string error = string.Empty;
            string msgText = string.Empty;

            if (File.Exists(scriptInfo.msgPath) || TryDecompileBMD(vanillaBmd, GetEncodingFromLang(scriptInfo.language), scriptInfo.msgPath, out error))
            {
                msgText = File.ReadAllText(scriptInfo.msgPath);
                fileTexts[scriptInfo.msgPath] = msgText;
                scriptInfo.flowPath = null;

                VanillaScripts.Add(scriptInfo);
            }
            scriptInfo.log = error;
        }

        if (!config.ReadOnly)
        {
            string moddedFileDir = config.ProjectManager.ModdedFileDir;
            bool locFrameworkEnabled = config.ProjectManager._hasFramework("LocalisationFramework");
            foreach (var moddedBf in config.EventManager.BfPaths)
            {
                ModdedScriptInfo scriptInfo = new(Path.GetRelativePath(moddedFileDir, moddedBf), "BF", false);
                scriptInfo.flowPath = Path.ChangeExtension(moddedBf, ".flow");
                scriptInfo.msgPath = Path.ChangeExtension(moddedBf, ".msg");

                string error = string.Empty;
                string flowText = string.Empty;
                string? msgText = null;

                if (File.Exists(scriptInfo.flowPath) || TryDecompileBF(moddedBf, this.Encoding, scriptInfo.flowPath, out error))
                {
                    flowText = File.ReadAllText(scriptInfo.flowPath);
                    if (File.Exists(scriptInfo.msgPath)) { msgText = File.ReadAllText(scriptInfo.msgPath); }
                    fileTexts[scriptInfo.flowPath] = flowText;
                    fileTexts[scriptInfo.msgPath] = msgText;

                    scriptInfo.localizedFlowPaths = locFrameworkEnabled ? FindLocalizedFiles(RemovePrefix(moddedFileDir, scriptInfo.flowPath)) : null;
                    scriptInfo.localizedMsgPaths = locFrameworkEnabled ? FindLocalizedFiles(RemovePrefix(moddedFileDir, scriptInfo.msgPath)) : null;
                    ModdedScripts.Add(scriptInfo);
                }
                scriptInfo.log = error;
            }

            foreach (var moddedBmd in config.EventManager.BmdPaths)
            {
                ModdedScriptInfo scriptInfo = new(Path.GetRelativePath(moddedFileDir, moddedBmd), "BMD", false);
                scriptInfo.msgPath = Path.ChangeExtension(moddedBmd, ".msg");
                string error = string.Empty;
                string msgText = string.Empty;

                if (File.Exists(scriptInfo.msgPath) || TryDecompileBMD(moddedBmd, this.Encoding, scriptInfo.msgPath, out error))
                {
                    msgText = File.ReadAllText(scriptInfo.msgPath);
                    fileTexts[scriptInfo.msgPath] = msgText;
                    scriptInfo.flowPath = null;

                    scriptInfo.localizedFlowPaths = null;
                    scriptInfo.localizedMsgPaths = locFrameworkEnabled ? FindLocalizedFiles(RemovePrefix(moddedFileDir, scriptInfo.msgPath)) : null;
                    ModdedScripts.Add(scriptInfo);
                }
                scriptInfo.log = error;
            }

            // find all .flow and .msg files associated with an emulated script
            // FEmulator routing allows for partial matching, so the relative path from FEmulator/BF (or BMD) to the file can be any substring of the cpk path where the filename matches (ignoring extensions)
            // in practice most people just put script files at the top level for that emulator (eg. FEmulator/BMD/filename.msg), we'll use this as default export path if no matching file exists
            // but mods with a lot of script files may use subfolders matching the route for organization
            bool bfEmuEnabled = config.ProjectManager._hasFramework("BFEmulator");
            string emulatedBfDir = config.ProjectManager.EmulatedBfDir;
            if (bfEmuEnabled)
            {
                foreach (var emulatedBf in config.EventManager.EmulatedBfPaths)
                {
                    var dummyRelativePath = Path.GetRelativePath(moddedFileDir, emulatedBf);
                    var scriptInfo = new ModdedScriptInfo(dummyRelativePath, "BF", true);
                    var routeWithoutCpk = Path.ChangeExtension(scriptInfo.pathInCpk, null);
                    var fileName = Path.GetFileName(routeWithoutCpk);

                    var filesForThisRoute = Directory.EnumerateFiles(emulatedBfDir, "*.*", SearchOption.AllDirectories).Where(x => routeWithoutCpk.EndsWith(Path.ChangeExtension(RemovePrefix(emulatedBfDir, x), null), StringComparison.InvariantCultureIgnoreCase));
                    scriptInfo.flowPath = filesForThisRoute.FirstOrDefault(x => Path.GetExtension(x) == ".flow", Path.Combine(emulatedBfDir, fileName) + ".flow");
                    scriptInfo.msgPath = filesForThisRoute.FirstOrDefault(x => Path.GetExtension(x) == ".msg", Path.Combine(emulatedBfDir, fileName) + ".msg");

                    fileTexts[scriptInfo.flowPath] = File.Exists(scriptInfo.flowPath) ? File.ReadAllText(scriptInfo.flowPath) : null;
                    fileTexts[scriptInfo.msgPath] = File.Exists(scriptInfo.msgPath) ? File.ReadAllText(scriptInfo.msgPath) : null;
                    scriptInfo.log = null;

                    scriptInfo.localizedFlowPaths = locFrameworkEnabled ? FindLocalizedFiles(RemovePrefix(emulatedBfDir, scriptInfo.flowPath)) : null;
                    scriptInfo.localizedMsgPaths = locFrameworkEnabled ? FindLocalizedFiles(RemovePrefix(emulatedBfDir, scriptInfo.msgPath)) : null;
                    ModdedScripts.Add(scriptInfo);
                }
            }

            bool bmdEmuEnabled = config.ProjectManager._hasFramework("BMDEmulator");
            string emulatedBmdDir = config.ProjectManager.EmulatedBmdDir;
            if (bmdEmuEnabled)
            {
                foreach (var emulatedBmd in config.EventManager.EmulatedBmdPaths)
                {
                    var dummyRelativePath = Path.GetRelativePath(moddedFileDir, emulatedBmd);
                    var scriptInfo = new ModdedScriptInfo(dummyRelativePath, "BMD", true);
                    var routeWithoutCpk = Path.ChangeExtension(scriptInfo.pathInCpk, ".msg");
                    var fileName = Path.GetFileName(routeWithoutCpk);

                    scriptInfo.flowPath = null;
                    scriptInfo.msgPath = Directory.EnumerateFiles(emulatedBmdDir, "*.*", SearchOption.AllDirectories).FirstOrDefault(x => routeWithoutCpk.EndsWith(RemovePrefix(emulatedBmdDir, x), StringComparison.InvariantCultureIgnoreCase), Path.Combine(emulatedBmdDir, fileName));
                    fileTexts[scriptInfo.msgPath] = File.Exists(scriptInfo.msgPath) ? File.ReadAllText(scriptInfo.msgPath) : null;
                    scriptInfo.log = null;

                    scriptInfo.localizedFlowPaths = null;
                    scriptInfo.localizedMsgPaths = locFrameworkEnabled ? FindLocalizedFiles(RemovePrefix(emulatedBmdDir, scriptInfo.msgPath)) : null;
                    ModdedScripts.Add(scriptInfo);
                }
            }

            // create info for files that don't already exist
            var evt = config.EventManager.SerialEvent;

            if (!ModdedScripts.Exists(s => s.scriptKind == "BF"))
            {
                var bf = new ModdedScriptInfo(Path.Combine("EN.CPK", (evt.EventBfPath is null) ? $"event_data/script/e{(100 * (evt.MajorId / 100)):000}/e{evt.MajorId:000}_{evt.MinorId:000}.bf" : evt.EventBfPath.Replace("\0", "")), "BF", bfEmuEnabled);
                bf.flowPath = bfEmuEnabled ? Path.Combine(emulatedBfDir, Path.GetFileNameWithoutExtension(bf.pathInCpk)) + ".flow" : Path.ChangeExtension(Path.Combine(moddedFileDir, bf.path), ".flow");
                bf.msgPath = Path.ChangeExtension(bf.flowPath, ".msg");
                fileTexts[bf.flowPath] = File.Exists(bf.flowPath) ? File.ReadAllText(bf.flowPath) : string.Empty;
                fileTexts[bf.msgPath] = File.Exists(bf.msgPath) ? File.ReadAllText(bf.msgPath) : string.Empty;
                bf.log = null;
                bf.localizedFlowPaths = locFrameworkEnabled ? new() : null;
                bf.localizedMsgPaths = locFrameworkEnabled ? new() : null;
                ModdedScripts.Add(bf);
            }

            if (!ModdedScripts.Exists(s => s.scriptKind == "BMD"))
            {
                var bmd = new ModdedScriptInfo(Path.Combine("EN.CPK", (evt.EventBmdPath is null) ? $"event_data/message/e{(100 * (evt.MajorId / 100)):000}/e{evt.MajorId:000}_{evt.MinorId:000}.bmd" : evt.EventBmdPath.Replace("\0", "")), "BMD", bmdEmuEnabled);
                bmd.flowPath = null;
                bmd.msgPath = bmdEmuEnabled ? Path.Combine(emulatedBmdDir, Path.GetFileNameWithoutExtension(bmd.pathInCpk)) + ".msg" : Path.ChangeExtension(Path.Combine(moddedFileDir, bmd.path), ".msg");
                fileTexts[bmd.msgPath] = File.Exists(bmd.msgPath) ? File.ReadAllText(bmd.msgPath) : string.Empty;
                bmd.log = null;
                bmd.localizedFlowPaths = null;
                bmd.localizedMsgPaths = locFrameworkEnabled ? new() : null;
                ModdedScripts.Add(bmd);
            }
        }

        // TODO: populate this.BMDFiles so that message preview in timeline tab works
        InitializeWorkingDir();
        ChangeActiveScripts();

        //ActiveBF = ModdedScripts.Exists(s => s.scriptKind == "BF") ? ModdedScripts.First(s => s.scriptKind == "BF").path : null;
        //ActiveBMD = ModdedScripts.Exists(s => s.scriptKind == "BMD") ? ModdedScripts.First(s => s.scriptKind == "BMD").path : null;
        //ActiveVanillaBF = VanillaScripts.Exists(s => s.scriptKind == "BF") ? VanillaScripts.First(s => s.scriptKind == "BF").path : null;
        //ActiveVanillaBMD = VanillaScripts.Exists(s => s.scriptKind == "BMD") ? VanillaScripts.First(s => s.scriptKind == "BMD").path : null;
    }

    private VanillaScriptInfo FindMatchingVanillaScript(ModdedScriptInfo moddedScript, string language)
    {
        VanillaScriptInfo vanillaScript = null;
        foreach (var lang in new List<string> { language, "Japanese"}) // i considered having english in here but an actual mod user should only have their language cpk and base in game folder
        {
            vanillaScript ??= VanillaScripts.FirstOrDefault(s => s.pathInCpk.Equals(moddedScript.pathInCpk, StringComparison.InvariantCultureIgnoreCase) && s.language == lang, null);
            if (vanillaScript != null)
                break;
            if (language == "Japanese") // avoid redundant checking
                break;
        }
        return vanillaScript;
    }

    public void ChangeActiveScripts()
    {
        if (config.ReadOnly)
        {
            if (VanillaScripts.Exists(s => s.scriptKind == "BF"))
                foreach (var lang in new List<string>() { config.Language, "Japanese"})
                {
                    var vanillaScript = VanillaScripts.FirstOrDefault(s => s.language == lang && s.scriptKind == "BF", null);
                    if (vanillaScript is not null)
                    {
                        ActiveBF = vanillaScript.path;
                        break;
                    }
                    if (config.Language == "Japanese") // avoid redundant checking
                        break;
                }

            if (VanillaScripts.Exists(s => s.scriptKind == "BMD"))
                foreach (var lang in new List<string>() { config.Language, "Japanese" })
                {
                    var vanillaScript = VanillaScripts.FirstOrDefault(s => s.language == lang && s.scriptKind == "BMD", null);
                    if (vanillaScript is not null)
                    {
                        ActiveBMD = vanillaScript.path;
                        break;
                    }
                    if (config.Language == "Japanese") // avoid redundant checking
                        break;
                }
        }
        else
        {
            var bf = ModdedScripts.FirstOrDefault(s => s.scriptKind == "BF", null);
            if (bf is not null)
                ActiveBF = Path.Combine(config.Language, bf.pathInCpk);

            var bmd = ModdedScripts.FirstOrDefault(s => s.scriptKind == "BMD", null);
            if (bmd is not null)
                ActiveBMD = Path.Combine(config.Language, bmd.pathInCpk);
        }
    }

    private void InitializeWorkingDir() // i feel like theres a better name for this but i cant think of it lol
    {
        if (config.ReadOnly)
        {
            foreach (var script in VanillaScripts)
            {
                if (script.scriptKind == "BMD")
                {
                    var bmd = new BMD();
                    bmd.Read(Path.Combine(config.VanillaExtractionPath, script.path));
                    this.BMDFiles[script.path] = bmd;
                }
                // TODO bfs lol
            }
            return;
        }

        string workingDir = config.WorkingPath;
        if (!Directory.Exists(workingDir))
            Directory.CreateDirectory(workingDir);
        foreach (var lang in LangCodeDict.Keys)
            Directory.CreateDirectory(Path.Combine(workingDir, lang));

        void UseVanillaScriptForPreview(ModdedScriptInfo script, string language)
        {
            VanillaScriptInfo vanillaScript = FindMatchingVanillaScript(script, language);
            if (vanillaScript == null)
                return;

            if (vanillaScript.scriptKind == "BMD")
            {
                var scriptPath = Path.Combine(workingDir, language, vanillaScript.pathInCpk);
                if (!Directory.Exists(Path.GetDirectoryName(scriptPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.Copy(Path.Combine(config.VanillaExtractionPath, vanillaScript.path), scriptPath, true);

                var bmd = new BMD();
                bmd.Read(scriptPath);
                this.BMDFiles[Path.Combine(language, script.pathInCpk)] = bmd;
            }
            // TODO bfs lol
        }

        foreach (var script in ModdedScripts)
        {
            if (script.scriptKind == "BMD")
            {
                if (script.isEmulated)
                {
                    foreach (var lang in LangCodeDict.Keys)
                    {
                        if (TryCompileBMD(script, lang, out var msgScript, out var log))
                        {
                            string key = Path.Combine(lang, script.pathInCpk);
                            var scriptPath = Path.Combine(workingDir, key);
                            msgScript.ToFile(scriptPath);

                            var bmd = new BMD();
                            bmd.Read(scriptPath);
                            BMDFiles[key] = bmd;
                        }
                        else
                            UseVanillaScriptForPreview(script, lang);
                    }
                }
                else
                {
                    var enKey = Path.Combine("English", script.pathInCpk);
                    var enScriptPath = Path.Combine(workingDir, enKey);
                    string pathInMod = Path.Combine(config.ProjectManager.ModdedFileDir, script.path);
                    if (!File.Exists(pathInMod))
                    {
                        if (!File.Exists(script.msgPath) || !TryCompileBMD(script, out var msgScript, out var log))
                        {
                            foreach (var lang in LangCodeDict.Keys)
                                UseVanillaScriptForPreview(script, lang);
                            continue;
                        }
                        msgScript.ToFile(enScriptPath);
                    }
                    if (!Directory.Exists(Path.GetDirectoryName(enScriptPath)))
                        Directory.CreateDirectory(Path.GetDirectoryName(enScriptPath));
                    File.Copy(pathInMod, enScriptPath, true);

                    var bmd = new BMD();
                    bmd.Read(enScriptPath);
                    BMDFiles[enKey] = bmd;

                    foreach (var (lang, langCode) in LangCodeDict)
                    {
                        if (lang == "English")
                            continue;

                        var key = Path.Combine(lang, script.pathInCpk);
                        var localizedScriptPath = Path.Combine(workingDir, key);
                        if (!Directory.Exists(Path.GetDirectoryName(localizedScriptPath)))
                            Directory.CreateDirectory(Path.GetDirectoryName(localizedScriptPath));

                        if (script.localizedMsgPaths is null || !script.localizedMsgPaths.ContainsKey(langCode))
                            File.Copy(enScriptPath, localizedScriptPath, true);
                        else if (File.Exists(Path.ChangeExtension(script.localizedFlowPaths[langCode], ".bmd")))
                            File.Copy(Path.ChangeExtension(script.localizedMsgPaths[langCode], ".bmd"), localizedScriptPath, true);
                        else if (TryCompileBMD(script, lang, out var msgScript, out var log))
                            msgScript.ToFile(localizedScriptPath);

                        var localizedBmd = new BMD();
                        localizedBmd.Read(localizedScriptPath);
                        BMDFiles[key] = localizedBmd;
                    }
                }

            }
            // TODO: serialize bfs lol
        }
    }

    public bool TryDecompileBMD(string bmd, string outputPath, out string log) => TryDecompileBMD(bmd, this.Encoding, outputPath, out log);
    public bool TryDecompileBMD(string bmd, AtlusEncoding encoding, string outputPath, out string log)
    {
        log = string.Empty;
        try
        {
            var msgScript = MessageScript.FromFile(bmd, MsgFormatVersion.Detect, this.Encoding);
            using (var decompiler = new MessageScriptDecompiler(new FileTextWriter(outputPath)))
            {
                decompiler.Library = LibraryLookup.GetLibrary(this.GameName);
                decompiler.Decompile(msgScript);
            }
            return true;
        }
        catch (Exception ex)
        {
            log = ex.ToString();
            Trace.TraceError(log);
            return false;
        }
    }

    public bool TryDecompileBF(string bf, string outputPath, out string log) => TryDecompileBF(bf, this.Encoding, outputPath, out log);
    public bool TryDecompileBF(string bf, AtlusEncoding encoding, string outputPath, out string log)
    {
        bool success;
        log = string.Empty;
        var listener = new AppLogListener();
        try
        {
            var flowScript = FlowScript.FromFile(bf, encoding);
            var decompiler = new FlowScriptDecompiler();
            decompiler.Library = LibraryLookup.GetLibrary(this.GameName);
            decompiler.AddListener(listener);

            success = decompiler.TryDecompile(flowScript, outputPath);
        }
        catch (Exception ex)
        {
            log = ex.ToString();
            Trace.TraceError(log);
            listener.Text += log;

            success = false;
        }
        log = listener.Text;
        return success;
    }

    /*public void PopulateWorkingDir()
    {
        var workingDir = config.WorkingPath;
        var baseDir = config.VanillaExtractionPath;
        var modDir = config.ProjectManager.ModdedFileDir;
        var emuDir = config.ProjectManager.EmulatedFileDir;

        var bfPaths = config.EventManager.BfPaths;
        var bmdPaths = config.EventManager.BmdPaths;
        var dummyFiles = new List<string>(config.EventManager.EmulatedBfPaths);
        dummyFiles.AddRange(config.EventManager.EmulatedBmdPaths);

        FindEmulatedFiles(modDir, emuDir, dummyFiles);

        // TODO: this is dumb, fix this with the file management PR
        foreach (List<string> pathList in new[] { bmdPaths, bfPaths })
            if (!(pathList is null))
                //for (int i = 0; i < pathList.Count; i++)
                // oh god this is bad and stupid. bandaid solution to go with the sort + reverse thing for the files
                // otherwise, if the vanilla and mod cpk names are the same and emulator is off, it gets ugly.
                // TODO TODO TODO oh god TODO
                for (int i = pathList.Count-1; i >= 0; i--)
                {
                    string workingPath = null;
                    if (pathList[i].StartsWith(baseDir))
                        workingPath = Path.Combine(workingDir, this.RemovePrefix(baseDir, pathList[i]));
                    else if (pathList[i].StartsWith(modDir))
                        workingPath = Path.Combine(workingDir, this.RemovePrefix(modDir, pathList[i]));
                    // copy BFs/BMDs to working dir
                    if (!Directory.Exists(Path.GetDirectoryName(workingPath)))
                        Directory.CreateDirectory(Path.GetDirectoryName(workingPath));
                    File.Copy(pathList[i], workingPath, true);
                    // make sure those are the ref paths
                    pathList[i] = workingPath;
                }

        this.UpdateScripts(bmdPaths, bfPaths, workingDir, config.ProjectManager.ActiveGame.Type);

        // THEN decompile them
        this.DecompileAll(workingDir);

        // and THEN copy modded decomp files over the fresh decomp'd ones (if emu is active)
        this.MaybeOverwriteScripts(workingDir, modDir, emuDir);
        this.RefreshScriptTexts(workingDir);

        // and then recompile with those to make sure we have the right binaries
        foreach (string fileBase in this.ScriptTexts["BMD"].Keys)
            this.CompileMessage(workingDir, fileBase);
    }*/

    /*public void UpdateScripts(List<string> bmdPaths, List<string> bfPaths, string modPath, string gameType)
    {
        // TODO: I gooootta handle game/locale/encoding more elegantly, lmao
        if (gameType.StartsWith("P5R"))
        {
            this.GameName = "p5r";
            this.EnCharLookup = CharLookup("P5R_EFIGS");
            this.JpCharLookup = CharLookup("P5R_Japanese");
        }
        else
        {
            this.GameName = "p5";
            this.EnCharLookup = CharLookup("P5");
            this.JpCharLookup = this.EnCharLookup;
        }
        this.Encoding = AtlusEncoding.GetByName(this.GameName); // TODO: AtlusEncoding.GetByName is replaced by AtlusEncoding.Create in upstream, which we'll probably have to move to if we want multilanguage support but idk how to update the batch script lol -ocean

        this.BMDFiles.Clear();
        //this.BFFiles.Clear();

        foreach (string scriptType in this.ScriptList.Keys)
        {
            this.ScriptList[scriptType].Clear();
            this.ScriptTexts[scriptType].Clear();
            this.ScriptErrors[scriptType].Clear();
            List<string> paths = (scriptType == "BMD") ? bmdPaths : bfPaths;
            if (!(paths is null))
                foreach (string scriptPath in paths)
                {
                    string key = this.RemovePrefix(modPath, scriptPath);
                    this.ScriptList[scriptType].Add(key);
                    if (scriptType == "BMD")
                    {
                        var messageFile = new BMD();
                        messageFile.Read(scriptPath);
                        this.BMDFiles[key] = messageFile;
                    }
                    // TODO: do same for BF once i make the class for it....
                }
        }

        if (this.ScriptList["BMD"].Count > 0)
            this.ActiveBMD = this.ScriptList["BMD"][0];
        else
            this.ActiveBMD = null;

        if (this.ScriptList["BF"].Count > 0)
            this.ActiveBF = this.ScriptList["BF"][0];
        else
            this.ActiveBF = null;
    }*/

    /*public void DecompileAll(string targetDir)
    {
        foreach (string script in this.ScriptList["BMD"].Where(s => !EmulatedScriptPaths["BMD"].ContainsKey(s)))
            this.DecompileMessage(targetDir, script);
        foreach (string script in this.ScriptList["BF"].Where(s => !EmulatedScriptPaths["BF"].ContainsKey(s)))
            this.DecompileScript(targetDir, script);
    }*/

    /*public void MaybeOverwriteScripts(string workingDir, string modDir, string emuDir)
    {
        if (modDir is null || emuDir is null)
            return;
        foreach (string scriptType in this.ScriptTexts.Keys)
            foreach (string fileBase in this.ScriptTexts[scriptType].Keys)
            {
                //Regex patt = new Regex($"{fileBase}$", RegexOptions.IgnoreCase),
                foreach (string fileExt in this.ScriptTexts[scriptType][fileBase].Keys)
                    // TODO: this breaks with custom CPK folder names... and also with case-sensitive names... blagh
                    //foreach (string path in Directory.GetFiles(modDir, "*.*", SearchOption.AllDirectories))
                    // recommended format is dummy file in essentials + top-level femu
                    if (File.Exists(Path.Combine(modDir, fileBase)) && File.Exists(Path.Combine(emuDir, scriptType, Path.GetFileNameWithoutExtension(fileBase))+fileExt))
                        File.Copy(Path.Combine(emuDir, scriptType, Path.GetFileNameWithoutExtension(fileBase))+fileExt, this.BasePath(workingDir, fileBase)+fileExt, true);
                    // ...but full-path femu is also fine
                    else if (File.Exists(this.BasePath(Path.Combine(emuDir, scriptType), fileBase)+fileExt))
                        File.Copy(this.BasePath(Path.Combine(emuDir, scriptType), fileBase)+fileExt, this.BasePath(workingDir, fileBase)+fileExt, true);
            }
    }*/

    /*public void RefreshScriptTexts(string targetDir)
    {
        foreach (string scriptType in this.ScriptTexts.Keys)
            foreach (string fileBase in this.ScriptTexts[scriptType].Keys)
                foreach (string fileExt in this.ScriptTexts[scriptType][fileBase].Keys)
                    this.ScriptTexts[scriptType][fileBase][fileExt] = File.ReadAllText(this.BasePath(targetDir, fileBase)+fileExt);
    }*/

    /*public void DecompileMessage(string targetDir, string fileBase)
    {
        AppLogListener listener = new AppLogListener();
        this.ScriptTexts["BMD"][fileBase] = new Dictionary<string, string>();
        this.ScriptErrors["BMD"][fileBase] = "";
        try
        {
            string outPath = this.BasePath(targetDir, fileBase);
            string baseExt = Path.GetExtension(fileBase);
            MessageScriptBinary binary = MessageScriptBinary.FromStream(this.BMDFiles[fileBase].ToStream());
            MessageScript msgScript = MessageScript.FromBinary(binary, FormatVersion.Detect, this.Encoding);
            using (var decompiler = new MessageScriptDecompiler(new FileTextWriter(outPath+".msg")))
            {
                decompiler.Library = LibraryLookup.GetLibrary(this.GameName);
                decompiler.Decompile(msgScript);
            }
            string[] fileNames = Directory.GetFiles(Path.GetDirectoryName(outPath));
            Array.Sort(fileNames);
            foreach (string fileName in fileNames)
                if (fileName != outPath+baseExt && fileName.StartsWith(outPath))
                    this.ScriptTexts["BMD"][fileBase][fileName.Substring(outPath.Length, fileName.Length-outPath.Length)] = File.ReadAllText(fileName);

            if (this.ScriptTexts["BMD"][fileBase].Count == 0)
                this.ScriptErrors["BMD"][fileBase] = "Decompilation seemed okay, but no files found....";
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            this.ScriptErrors["BMD"][fileBase] = ex.ToString();
        }
    }*/

    /*public string CompileMessage(string targetDir, string fileBase)
    {
        string outPath = this.BasePath(targetDir, fileBase);
        foreach (string ext in this.ScriptTexts["BMD"][fileBase].Keys)
            File.WriteAllText(outPath+ext, this.ScriptTexts["BMD"][fileBase][ext]);
                
        MessageScriptCompiler compiler = new MessageScriptCompiler(FormatVersion.Version1BigEndian, this.Encoding);
        //MessageScriptCompiler compiler = new MessageScriptCompiler(FormatVersion.Version1, this.Encoding);
        compiler.Library = LibraryLookup.GetLibrary(this.GameName);
        AppLogListener listener = new AppLogListener();
        compiler.AddListener(listener);
        try
        {
            MessageScript msgScript = new MessageScript(FormatVersion.Version1BigEndian, this.Encoding);
            //MessageScript msgScript = new MessageScript(FormatVersion.Version1, this.Encoding);
            bool success = compiler.TryCompile(this.ScriptTexts["BMD"][fileBase][".msg"], out msgScript);
            // TODO... get emulation actually working here!
            //bool success = compiler.TryCompileWithImports(this.ScriptTexts["BMD"][fileBase][".msg"], List<string> imports, out msgScript);
            this.BMDFiles[fileBase] = new BMD();
            byte[] newBytes = ((MemoryStream)msgScript.ToBinary().ToStream()).ToArray();
            this.BMDFiles[fileBase].FromBytes(newBytes);
            File.WriteAllBytes(outPath+".BMD", newBytes);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
        }
        return listener.Text;
    }*/

    /*public void DecompileScript(string targetDir, string fileBase)
    {
        AppLogListener listener = new AppLogListener();
        this.ScriptTexts["BF"][fileBase] = new Dictionary<string, string>();
        this.ScriptErrors["BF"][fileBase] = "";
        try
        {
            string outPath = this.BasePath(targetDir, fileBase);
            string baseExt = Path.GetExtension(fileBase);
            FlowScriptBinary binary = FlowScriptBinary.FromStream(new FileStream(outPath+baseExt, FileMode.Open));
            FlowScript flowScript = FlowScript.FromBinary(binary, this.Encoding);
            var decompiler = new FlowScriptDecompiler();
            decompiler.Library = LibraryLookup.GetLibrary(this.GameName);
            decompiler.AddListener(listener);
            bool success = decompiler.TryDecompile(flowScript, outPath+".flow");
            if (success)
            {
                string[] fileNames = Directory.GetFiles(Path.GetDirectoryName(outPath));
                Array.Sort(fileNames);
                foreach (string fileName in fileNames)
                    if (fileName != outPath+baseExt && fileName.StartsWith(outPath))
                        this.ScriptTexts["BF"][fileBase][fileName.Substring(outPath.Length, fileName.Length-outPath.Length)] = File.ReadAllText(fileName);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
        }
        this.ScriptErrors["BF"][fileBase] = listener.Text;
    }*/

    /*public string CompileScript(string targetDir, string fileBase)
    {
        AppLogListener listener = new AppLogListener();
        string outPath = this.BasePath(targetDir, fileBase);
        foreach (string ext in this.ScriptTexts["BF"][fileBase].Keys)
            File.WriteAllText(outPath+ext, this.ScriptTexts["BF"][fileBase][ext]);

        string oldWorkingDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetDirectoryName(outPath));

        try
        {
            // TODO: should detect the version from the vanilla extracted file...?
            // orrrr output version just depends on game type?
            FlowScriptCompiler compiler = new FlowScriptCompiler(FlowFormatVersion.Version3BigEndian);
            compiler.Encoding = this.Encoding;
            compiler.Library = LibraryLookup.GetLibrary(this.GameName);
            // no idea why this tracing bool alone is set to true by default, but it is ANNOYING!!!
            compiler.EnableProcedureTracing = false;
            compiler.AddListener(listener);
            FlowScript flowScript = new FlowScript(FlowFormatVersion.Version3BigEndian);
            bool success = compiler.TryCompile(this.ScriptTexts["BF"][fileBase][".flow"], out flowScript);
            byte[] newBytes = ((MemoryStream)flowScript.ToBinary().ToStream()).ToArray();
            //this.BFFiles[fileBase] = new BF();
            //this.BFFiles[fileBase].FromBytes(newBytes);
            File.WriteAllBytes(outPath+".BF", newBytes);
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
            listener.Text += ex.ToString();
        }
        Directory.SetCurrentDirectory(oldWorkingDir);
        return listener.Text;
    }*/

    public int GetTurnIndex(Int16 majorId, byte minorId, byte _subId)
    {
        if (!(this.ActiveBMD is null))
            // it works even without an exact subId match, so... fallback logic
            foreach (byte subId in new[]{_subId, 0})
            {
                for (int i=0; i<this.BMDFiles[this.ActiveBMD].Turns.Length; i++)
                {
                    string name = "";
                    foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[i].Name, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                        name += node.Text;
                    if (Regex.IsMatch(name, $"^[A-Z][A-Z][A-Z]_{majorId:000}_{minorId}_{subId}$"))
                        return i;
                }
            }
        return -1;
    }

    public int GetTurnIndex(string targetName)
    {
        if (!(this.ActiveBMD is null))
            for (int i=0; i<this.BMDFiles[this.ActiveBMD].Turns.Length; i++)
            {
                string name = "";
                foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[i].Name, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                    name += node.Text;
                if (name == targetName)
                    return i;
            }
        return -1;
    }

    public string GetTurnName(int turnIndex)
    {
        string name = "";
        if (!(this.ActiveBMD is null))
            if (turnIndex >= 0 && turnIndex < this.BMDFiles[this.ActiveBMD].Turns.Length)
                foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[turnIndex].Name, this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                    name += node.Text;
        return name;
    }

    public string GetTurnSpeakerName(int turnIndex)
    {
        string name = "";
        if (!(this.ActiveBMD is null))
            if (turnIndex >= 0 && turnIndex < this.BMDFiles[this.ActiveBMD].Turns.Length && this.BMDFiles[this.ActiveBMD].Turns[turnIndex].SpeakerId != 0xFFFF)
                foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Speakers[this.BMDFiles[this.ActiveBMD].Turns[turnIndex].SpeakerId], this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                    name += node.Text;
        return name;
    }

    public int GetTurnElemCount(int turnIndex)
    {
        if (!(this.ActiveBMD is null))
            if (turnIndex >= 0 && turnIndex < this.BMDFiles[this.ActiveBMD].Turns.Length)
                return this.BMDFiles[this.ActiveBMD].Turns[turnIndex].ElemCount;
        return 0;
    }

    public string GetTurnText(int turnIndex, int elemIndex)
    {
        string text = "";
        if (!(this.ActiveBMD is null))
            if (turnIndex >= 0 && turnIndex < this.BMDFiles[this.ActiveBMD].Turns.Length)
                foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[turnIndex].Elems[elemIndex], this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                    text += node.Text;
        return text;
    }

    public (string Source, uint CueId)? GetTurnVoice(int turnIndex, int elemIndex)
    {
        if (!(this.ActiveBMD is null))
            foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[turnIndex].Elems[elemIndex], this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                if (node.FunctionTableIndex == 3 && node.FunctionIndex == 1 && node.FunctionArguments[3] != 0)
                    switch (node.FunctionArguments[1])
                    {
                        case 0:
                            if (this.GetTurnSpeakerName(turnIndex).StartsWith("(SE)"))
                                return ("SFX", node.FunctionArguments[3]);
                            else
                                return ("Voice", node.FunctionArguments[3]);
                        case 1:
                            return ("Field", node.FunctionArguments[3]);
                        case 2:
                            return ("Common", node.FunctionArguments[3]);
                        default:
                            break;
                    }
        return null;
    }

    public (string[] Prefix, string Suffix) GetTurnBustupPath(int turnIndex, int elemIndex)
    {
        if (!(this.ActiveBMD is null))
            foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[turnIndex].Elems[elemIndex], this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                if (node.FunctionTableIndex == 4 && node.FunctionIndex == 6)
                {
                    int characterId = (int)node.FunctionArguments[1];
                    int emoteId = (int)node.FunctionArguments[2];
                    int outfitId = -1;
                    if (node.FunctionArguments[3] != 0xFFFF)
                        outfitId = (int)node.FunctionArguments[3];
                    bool isOnCall = (node.FunctionArguments[4] != 0);

                    if (outfitId == -1)
                        return (new string[] { "bustup" }, $"b{characterId:000}_{emoteId:000}_[0-9][0-9]\\.bin");
                    else
                        return (new string[] { "bustup", $"b{characterId:000}_{emoteId:000}_{outfitId:00}.bin" }, null);
                }
        return (null, null);
    }

    public MagickImage GetMainBustupImage(string[] prefix, string suffix)
    {
        List<string> paths = config.ExtractExactFiles(prefix, suffix);
        if (paths.Count > 0)
        {
            AtlusArchive bin = new AtlusArchive();
            bin.Read(paths[0]);
            foreach (FileEntry entry in bin.Entries)
            {
                MagickReadSettings settings = new MagickReadSettings() { Format = MagickFormat.Dds };
                if (entry.Name.Replace("\0", "").Trim().EndsWith(".dds2"))
                {
                    AtlusArchive dds2 = new AtlusArchive();
                    dds2.FromBytes(entry.Data);
                    using MagickImageCollection images = new MagickImageCollection();
                    foreach (FileEntry dds in dds2.Entries)
                        images.Add(new MagickImage(new MemoryStream(dds.Data), settings));
                    return (MagickImage)images.Flatten(MagickColors.None);
                }
                else
                    return new MagickImage(new MemoryStream(entry.Data), settings);
            }
        }
        return null;
    }

    public (string[] Prefix, string Suffix) GetTurnCutinPath(int turnIndex, int elemIndex)
    {
        if (!(this.ActiveBMD is null))
            foreach (Node node in this.Parse(this.BMDFiles[this.ActiveBMD].Turns[turnIndex].Elems[elemIndex], this.ActiveBMD.Contains("JP.CPK") || this.ActiveBMD.Contains("BASE.CPK")))
                // it's a cutin command AND it's visible
                if (node.FunctionTableIndex == 4 && node.FunctionIndex == 17 && node.FunctionArguments[11] == 1)
                {
                    int category = 0;
                    int majorId = 0;
                    if (node.FunctionArguments[8] >= 200)
                    {
                        category = 2;
                        majorId = (int)node.FunctionArguments[8] - 200;
                    }
                    else if (node.FunctionArguments[8] >= 100)
                    {
                        category = 1;
                        majorId = (int)node.FunctionArguments[8] - 100;
                    }
                    else
                        majorId = (int)node.FunctionArguments[8];

                    int minorId = (int)node.FunctionArguments[9];

                    int subId = 0;
                    if ((node.FunctionArguments[10] & 2) == 1)
                        subId = 2;
                    else if ((node.FunctionArguments[10] & 1) == 1)
                        subId = 1;

                    if (category == 2)
                        return (new string[] { "cutin", "utarc", "2_sub_0", $"cutinsub_0_{majorId:00}_{minorId:00}.{subId:000}" }, null);
                    else if (category == 1)
                        return (new string[] { "cutin", "utarc", "1_com_0", $"cutincom_0_{majorId:00}_{minorId:00}.{subId:000}" }, null);
                    else
                        return (new string[] { "cutin", "utarc", "0_main_0", $"cutinmain_0_{majorId:00}_{minorId:00}.{subId:000}" }, null);
                }
        return (null, null);
    }

    public MagickImage GetMainCutinImage(string[] prefix, string suffix)
    {
        List<string> paths = config.ExtractExactFiles(prefix, suffix);
        if (paths.Count > 0)
        {
            AtlusArchive bin = new AtlusArchive(isCutin: true);
            bin.Read(paths[0]);
            foreach (FileEntry entry in bin.Entries)
            {
                GLH thing = new GLH();
                thing.FromBytes(entry.Data);
                var settings = new MagickReadSettings() { Format = MagickFormat.Dds };
                return new MagickImage(new MemoryStream(thing.DecompressedData.Data), settings);
            }
        }
        return null;
    }

    /////////////////////////////
    // *** PRIVATE METHODS *** //
    /////////////////////////////
    private static Dictionary<(byte, byte), string> CharLookup(string encoding)
    {
        Dictionary<(byte, byte), string> charLookup = new Dictionary<(byte, byte), string>();
        List<string> charSet = ReadCharTable(encoding);
        for (int charIndex=0; charIndex<charSet.Count; charIndex++)
        {
            int glyphIndex = (charIndex + 0x60);
            int tableIndex = ((glyphIndex / 0x80) - 1);
            int tableRelativeIndex = (glyphIndex - (tableIndex * 0x80));
            charLookup.Add(((byte)(0x80 | tableIndex), (byte)tableRelativeIndex), charSet[charIndex]);
        }
        return charLookup;
    }

    private static List<string> ReadCharTable(string encoding)
    {
        string filename = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Charsets", $"{encoding}.tsv");
        List<string> charSet = new List<string>();
        foreach (string line in File.ReadLines(filename))
            foreach (string elem in line.Split("\t"))
                charSet.Add(System.Uri.UnescapeDataString(elem));
        return charSet;
    }

    private List<Node> Parse(byte[] unencoded, bool isJP)
    {
        List<Node> nodes = new List<Node>();
        Queue<byte> buffer = new Queue<byte>(unencoded);
        while (buffer.Count > 0)
        {
            byte b1 = buffer.Dequeue();
            if ((b1 & 0xF0) == 0xF0)
                nodes.Add(new Node(b1, buffer));
            else
            {
                Queue<byte> textbuff = new Queue<byte>();
                while (true)
                {
                    textbuff.Enqueue(b1);
                    if ((b1 & 0x80) == 0x80)
                        textbuff.Enqueue(buffer.Dequeue());
                    if (buffer.Count <= 0)
                        break;
                    b1 = buffer.Peek();
                    if ((b1 == 0) || (b1 == 0x0A) || ((b1 & 0xF0) == 0xF0))
                        break;
                    else
                       buffer.Dequeue();
                }
                nodes.Add(new Node(this.Encode(textbuff, isJP)));
            }
        }
        return nodes;
    }

    private struct Node
    {
        public Node(byte b1, Queue<byte> buffer)
        {
            FunctionId            = ((b1 << 8) | buffer.Dequeue());
            FunctionTableIndex    = ((FunctionId & 0xE0) >> 5);
            FunctionIndex         = (FunctionId & 0x1F);
            FunctionArgumentCount = ((((FunctionId >> 8) & 0xF) - 1) * 2);
            FunctionArguments     = new List<ushort>();
            for (int i=0; i<FunctionArgumentCount/2; i++)
                FunctionArguments.Add((ushort)((((buffer.Dequeue() - 1) & ~0xFF00) | (((buffer.Dequeue() - 1) << 8) & 0xFF00)) & 0xFFFF));
        }

        public Node(string text)
        {
            Text = text;
        }

        public int FunctionId;
        public int FunctionTableIndex;
        public int FunctionIndex;
        public int FunctionArgumentCount;
        public List<ushort> FunctionArguments;

        public string Text;
    }

    private string Encode(Queue<byte> unencoded, bool isJP)
    {
        string encoded = "";
        while (unencoded.Count > 0)
        {
            byte high = unencoded.Dequeue();
            if ((high & 0x80) == 0x80)
                try
                {
                    if (isJP)
                        encoded += this.JpCharLookup[(high, unencoded.Dequeue())];
                    else
                        encoded += this.EnCharLookup[(high, unencoded.Dequeue())];
                }
                catch (KeyNotFoundException ex)
                {
                    Trace.TraceError(ex.ToString());
                    encoded += "*";
                }
            else if (high != 0)
                encoded += (char)high;
        }
        return encoded;
    }

}
