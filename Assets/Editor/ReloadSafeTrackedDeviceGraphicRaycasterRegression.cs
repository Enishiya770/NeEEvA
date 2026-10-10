using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Presentation;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

/// <summary>Isolated real Play Mode and assembly-reload lifecycle regression.
/// SessionState carries only the test coordinator across two real domain reloads.
/// No headset, tracked interactor, model, audio or application scene is required.</summary>
[InitializeOnLoad]
public static class ReloadSafeTrackedDeviceGraphicRaycasterRegression
{
    private const string Key="Neva.XRRaycasterReloadRegression.";
    private const string RootName="Reload-safe raycaster lifecycle fixture";
    private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    private static readonly string DomainId=Guid.NewGuid().ToString("N");
    private static bool handlingFailure;

    static ReloadSafeTrackedDeviceGraphicRaycasterRegression()
    {
        EditorApplication.update+=Step;
        Application.logMessageReceived+=OnLog;
    }

    public static void RunBatch()
    {
        if(!Application.isBatchMode||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Run the raycaster lifecycle regression only in an isolated validation project.");
        string[] args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"-nevaXRReloadOutput");
        string output=Path.GetFullPath(index>=0&&index+1<args.Length?args[index+1]:"Logs/xr-raycaster-reload");
        Directory.CreateDirectory(output);
        SessionState.SetString(Key+"Output",output);SessionState.SetBool(Key+"Active",true);
        SessionState.SetString(Key+"Phase","enter-play");SessionState.SetInt(Key+"Reloads",0);
        SessionState.SetFloat(Key+"Deadline",(float)EditorApplication.timeSinceStartup+150f);
        Save(new JObject { ["status"]="running",["checks"]=new JArray(),["reloads"]=new JArray(),["errors"]=new JArray(),
            ["unityVersion"]=Application.unityVersion,
            ["scope"]="Actual isolated Play Mode, two EditorUtility.RequestScriptReload domain reloads, production Companion canvas creation and raycaster enable/disable/destroy. One separately labelled controlled registration-loss test. Private reflection is test-only. No real headset/ray/poke interaction or application scene is claimed." });
        try
        {
            var package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TrackedDeviceGraphicRaycaster).Assembly);
            var report=Read();report["xriVersion"]=package?.version;Save(report);
            Check(package?.version=="2.6.5","Pinned XRI 2.6.5 is the inspected lifecycle implementation; package upgrades require adapter review.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            EditorApplication.isPlaying=true;
        }
        catch(Exception error){Fail(error);}
    }

    private static void Step()
    {
        if(!SessionState.GetBool(Key+"Active",false)||EditorApplication.isCompiling||handlingFailure)return;
        try
        {
            if(EditorApplication.timeSinceStartup>SessionState.GetFloat(Key+"Deadline",float.MaxValue))
                throw new TimeoutException("Raycaster domain reload regression exceeded its total deadline.");
            string phase=SessionState.GetString(Key+"Phase","");
            if(phase=="enter-play"&&EditorApplication.isPlaying)
            {
                CreateFixture();NormalLifecycleChecks();RequestReload();
            }
            else if(phase=="waiting-reload"&&EditorApplication.isPlaying&&SessionState.GetString(Key+"PreviousDomain","")!=DomainId)
            {
                AfterRealReload();
                if(SessionState.GetInt(Key+"Reloads",0)<2)RequestReload();
                else
                {
                    ControlledDisableFirstCheck();
                    SessionState.SetString(Key+"Phase","exit-play");
                    EditorApplication.isPlaying=false;
                }
            }
            else if(phase=="exit-play"&&!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Check(HoverRegistry.Count==0,"Play exit destroys every fixture raycaster and leaves no static hover registration.");
                Check(!Resources.FindObjectsOfTypeAll<ReloadSafeTrackedDeviceGraphicRaycaster>().Any(x=>x!=null&&x.gameObject.scene.IsValid()),
                    "No Play fixture component survives into Edit Mode.");
                Finish(true);
            }
        }
        catch(Exception error){Fail(error);}
    }

    private static void CreateFixture()
    {
        var root=new GameObject(RootName);
        var camera=new GameObject("Reload fixture camera",typeof(Camera)).GetComponent<Camera>();camera.transform.SetParent(root.transform);
        Canvas MakeCanvas(string name,bool active=true)
        {
            var go=new GameObject(name);go.SetActive(active);go.transform.SetParent(root.transform);
            var canvas=go.AddComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=camera;return canvas;
        }
        var hud=MakeCanvas("Reload HUD");var panel=MakeCanvas("Reload panel");
        var companion=root.AddComponent<CompanionXRInteraction>();companion.Configure(camera,new[]{hud,panel});
        Check(hud.GetComponent<TrackedDeviceGraphicRaycaster>() is ReloadSafeTrackedDeviceGraphicRaycaster&&
            panel.GetComponent<TrackedDeviceGraphicRaycaster>() is ReloadSafeTrackedDeviceGraphicRaycaster,
            "Production CompanionXRInteraction creates the local lifecycle adapter on both canvases.");
        MakeCanvas("Reload disabled component").gameObject.AddComponent<ReloadSafeTrackedDeviceGraphicRaycaster>().enabled=false;
        var inactive=MakeCanvas("Reload inactive object");inactive.gameObject.AddComponent<ReloadSafeTrackedDeviceGraphicRaycaster>();inactive.gameObject.SetActive(false);
        MakeCanvas("Reload never active",false).gameObject.AddComponent<ReloadSafeTrackedDeviceGraphicRaycaster>();
        foreach(string name in new[]{"Reload HUD","Reload panel","Reload disabled component","Reload inactive object"})CheckInitialized(Find(name),"initial "+name);
        Check(!HoverRegistry.Contains(Find("Reload never active")),"A never-active object is not initialized prematurely by the fixture.");
    }

    private static void NormalLifecycleChecks()
    {
        var ray=Find("Reload HUD");object poke=BaseField("m_PokeLogic").GetValue(ray);int count=HoverRegistry.Count;
        for(int i=0;i<3;i++){ray.enabled=false;ray.enabled=true;ray.gameObject.SetActive(false);ray.gameObject.SetActive(true);}
        Check(HoverRegistry.Count==count&&ReferenceEquals(poke,BaseField("m_PokeLogic").GetValue(ray)),
            "Ordinary disable/re-enable preserves one registration and one poke initialization instead of calling base.Awake twice.");
        var disposable=new GameObject("Reload destroy control",typeof(Canvas));
        var destroyed=disposable.AddComponent<ReloadSafeTrackedDeviceGraphicRaycaster>();CheckInitialized(destroyed,"before active destroy");
        Object.DestroyImmediate(disposable);
        Check(!HoverRegistry.Contains(destroyed)&&HoverRegistry.Count==count,"Active destruction uses the package unregister path exactly once.");
        var never=new GameObject("Reload never-awake destroy");never.SetActive(false);never.AddComponent<Canvas>();
        var dormant=never.AddComponent<ReloadSafeTrackedDeviceGraphicRaycaster>();Object.DestroyImmediate(never);
        Check(!HoverRegistry.Contains(dormant)&&HoverRegistry.Count==count,"Destroying a never-active component does not invent or leak a registration.");
    }

    private static void RequestReload()
    {
        int number=SessionState.GetInt(Key+"Reloads",0)+1;
        SessionState.SetInt(Key+"Reloads",number);SessionState.SetString(Key+"PreviousDomain",DomainId);
        SessionState.SetString(Key+"Phase","waiting-reload");
        var report=Read();((JArray)report["reloads"]).Add(new JObject { ["number"]=number,["requestedFromDomain"]=DomainId,["actualDomainChanged"]=false });Save(report);
        EditorUtility.RequestScriptReload();
    }

    private static void AfterRealReload()
    {
        Check(EditorApplication.isPlaying,"Requested script reload returns to the same actual Play session.");
        Check(SessionState.GetString(Key+"PreviousDomain","")!=DomainId,"Managed static domain identity changed through a real script reload.");
        foreach(string name in new[]{"Reload HUD","Reload panel"})CheckInitialized(Find(name),"after actual reload "+name);
        // Previously disabled components may not receive OnEnable during reload.
        // Their next actual enable must restore the same complete package state.
        var disabled=Find("Reload disabled component");disabled.enabled=true;CheckInitialized(disabled,"enable disabled component after reload");disabled.enabled=false;
        var inactive=Find("Reload inactive object");inactive.gameObject.SetActive(true);CheckInitialized(inactive,"activate inactive object after reload");inactive.gameObject.SetActive(false);
        var dormant=Find("Reload never active");dormant.gameObject.SetActive(true);CheckInitialized(dormant,"first activation across reload");dormant.gameObject.SetActive(false);
        NormalLifecycleChecks();
        var report=Read();var row=(JObject)((JArray)report["reloads"]).Last;row["returnedDomain"]=DomainId;row["actualDomainChanged"]=true;
        row["hoverRegistrations"]=HoverRegistry.Count;Save(report);
    }

    private static void ControlledDisableFirstCheck()
    {
        var ray=Find("Reload HUD");int count=HoverRegistry.Count;
        // This is NOT a real reload: simulate the specific missing per-domain
        // registration/initialization before OnDisable, while retaining all other
        // live raycasters. Actual reloads are independently covered above.
        HoverRegistry.Remove(ray);
        typeof(ReloadSafeTrackedDeviceGraphicRaycaster).GetField("initializedInDomain",Instance).SetValue(ray,false);
        BaseField("m_PokeLogic").SetValue(ray,null);
        var physics=BaseField("m_LocalPhysicsScene");physics.SetValue(ray,Activator.CreateInstance(physics.FieldType));
        ray.enabled=false;
        CheckInitialized(ray,"controlled lost domain state followed by OnDisable first");
        Check(HoverRegistry.Count==count,"Controlled disable-first initialization restores only this raycaster's missing entry.");
        ray.enabled=true;CheckInitialized(ray,"enable after controlled disable-first restore");
        var report=Read();report["controlledLoss"]="Test-only reflection removed this entry and reset its initialization/poke/physics, then real component disable restored full state. This is not represented as a real assembly reload or actual XR interaction.";Save(report);
    }

    private static ReloadSafeTrackedDeviceGraphicRaycaster Find(string name)
        => Resources.FindObjectsOfTypeAll<ReloadSafeTrackedDeviceGraphicRaycaster>().Single(x=>x!=null&&x.name==name&&x.gameObject.scene.IsValid());
    private static FieldInfo BaseField(string name)=>typeof(TrackedDeviceGraphicRaycaster).GetField(name,Instance);
    private static IDictionary HoverRegistry=>(IDictionary)typeof(TrackedDeviceGraphicRaycaster).GetField("s_PokeHoverRaycasters",Static).GetValue(null);
    private static void CheckInitialized(ReloadSafeTrackedDeviceGraphicRaycaster ray,string label)
    {
        object bindings=ray==null?null:BaseField("m_BindingsGroup").GetValue(ray);
        var subscriptions=bindings?.GetType().GetField("m_Bindings",Instance)?.GetValue(bindings) as IList;
        Check(ray!=null&&HoverRegistry.Contains(ray)&&ray.pokeStateData!=null&&BaseField("m_PokeLogic").GetValue(ray)!=null&&
            subscriptions?.Count==1&&((PhysicsScene)BaseField("m_LocalPhysicsScene").GetValue(ray)).IsValid(),
            label+": hover registration, poke state with one binding and local physics are initialized.");
    }
    private static JObject Read()=>JObject.Parse(SessionState.GetString(Key+"Report","{}"));
    private static void Save(JObject report)
    {
        SessionState.SetString(Key+"Report",report.ToString());
        File.WriteAllText(Path.Combine(SessionState.GetString(Key+"Output","Logs/xr-raycaster-reload"),"report.json"),report.ToString()+"\n");
    }
    private static void Check(bool condition,string description)
    {
        if(!condition)throw new InvalidOperationException(description);
        var report=Read();((JArray)report["checks"]).Add(description);Save(report);
    }
    private static void OnLog(string message,string stack,LogType type)
    {
        if(!SessionState.GetBool(Key+"Active",false)||handlingFailure||(type!=LogType.Exception&&type!=LogType.Error))return;
        var report=Read();((JArray)report["errors"]).Add(new JObject { ["message"]=message,["stack"]=stack,["phase"]=SessionState.GetString(Key+"Phase","") });Save(report);
    }
    private static void Fail(Exception error)
    {
        handlingFailure=true;
        var report=Read();report["failure"]=error.ToString();Save(report);Finish(false);
    }
    private static void Finish(bool succeeded)
    {
        var report=Read();bool passed=succeeded&&((JArray)report["errors"]).Count==0;
        report["status"]=passed?"passed":"failed";report["checkCount"]=((JArray)report["checks"]).Count;Save(report);
        SessionState.SetBool(Key+"Active",false);
        Debug.Log("[XRRaycasterReloadRegression] "+report["status"]+" "+report["checkCount"]+" checks; "+SessionState.GetString(Key+"Output","") );
        EditorApplication.Exit(passed?0:1);
    }
}
