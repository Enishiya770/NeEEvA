using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class PersonalSingingLearningWaveformRegression
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool good, string message) { if (!good) throw new InvalidOperationException(message); checks++; }
        JObject Region(int a, int b, string label) => new JObject { ["start_sample"] = a, ["end_sample"] = b, ["label"] = label };
        var root = new GameObject("Waveform pointer fixture", typeof(RectTransform), typeof(Canvas));
        root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var obj = new GameObject("Waveform fixture", typeof(RectTransform)); obj.transform.SetParent(root.transform, false);
        var wave = obj.AddComponent<PersonalSingingLearningWaveform>(); wave.rectTransform.sizeDelta = new Vector2(648,174);
        var samples = new float[300]; for (int i = 0; i < samples.Length; i++) samples[i] = Mathf.Sin(i * .3f) * .5f;
        try
        {
            wave.SetAudio(samples,100); wave.SetRegions(new JArray(Region(0,100,"speech"),Region(100,200,"singing"),Region(200,300,"speech")));
            Canvas.ForceUpdateCanvases();
            Check(wave.GetComponent<CanvasRenderer>() != null, "waveform must create its renderer before being displayed");
            Vector2 Screen(int sample) { var r=wave.rectTransform.rect; return RectTransformUtility.WorldToScreenPoint(null, wave.rectTransform.TransformPoint(new Vector3(r.xMin+8+(r.width-16)*sample/300f,r.center.y))); }
            var pointer = new PointerEventData(null) { pointerId=42, button=PointerEventData.InputButton.Left, position=Screen(150) };
            wave.OnPointerDown(pointer); wave.OnPointerUp(pointer);
            Check(wave.SelectionStart==100 && wave.SelectionEnd==200, "click selects the acoustic interval under pointer");
            int boundaryCalls=0; wave.BoundaryChanged += (a,b,c,d) => {
                Check(a==100 && b==200 && c==120 && d==200,"handle reports original and edited source samples"); boundaryCalls++;
                wave.SetRegions(new JArray(Region(0,100,"speech"),Region(100,120,"uncertain"),Region(120,200,"singing"),Region(200,300,"speech")));
            };
            pointer.position=Screen(100); wave.OnPointerDown(pointer); wave.OnBeginDrag(pointer); pointer.position=Screen(120); wave.OnDrag(pointer); wave.OnEndDrag(pointer); wave.OnPointerUp(pointer);
            Check(boundaryCalls==1 && wave.SelectionStart==120,"drag commits once even when EndDrag and PointerUp both arrive");
            wave.SelectSpan(100,200); wave.SetRegions(new JArray(Region(0,300,"singing")));
            Check(wave.SelectionStart==0 && wave.SelectionEnd==300,"merged class rebinds handles to current region for another correction");
            wave.SelectAll(); wave.ZoomSelection(); wave.ShowAll();
            using(var vh=new VertexHelper())
            {
                typeof(PersonalSingingLearningWaveform).GetMethod("OnPopulateMesh",BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(VertexHelper)},null).Invoke(wave,new object[]{vh});
                var mesh=new Mesh(); vh.FillMesh(mesh); var bounds=wave.rectTransform.rect;
                Check(mesh.vertexCount>100,"real PCM yields waveform geometry");
                foreach(var point in mesh.vertices) if(float.IsNaN(point.x)||point.x<bounds.xMin||point.x>bounds.xMax||point.y<bounds.yMin||point.y>bounds.yMax) throw new InvalidOperationException("waveform or handles leave visible bounds");
                checks++; UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        return checks;
    }
}
