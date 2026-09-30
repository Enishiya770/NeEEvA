using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

// Explicit invocation only; inference outputs are NEVER executed or sent to TTS.
// Profiles and inputs are synthetic fixtures, not a full Unity latency test.
public static class FormalReplyBenchmark
{
    public static void Main(string[] args) => Run(args).GetAwaiter().GetResult();
    private static async System.Threading.Tasks.Task Run(string[] args)
    {
        var profiles=JObject.Parse(File.ReadAllText(args[0]));
        string output=args[1], root=args[2]; Directory.CreateDirectory(output);
        string singing=File.ReadAllText(Path.Combine(root,"Assets/AIChatTookit/Prompts/Skills/singing.txt"));
        using(var client=new HttpClient(new HttpClientHandler { UseProxy=false }) { Timeout=TimeSpan.FromSeconds(90) })
        {
            client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","ollama");
            var rows=new JArray();
            // Reverse order on the second pair; keep every outcome. No best-retry selection.
            foreach(var spec in new[]{"general:original","general:general","general:general","general:original",
                "spatial:original","spatial:spatial","spatial:spatial","spatial:original"})
            {
                var parts=spec.Split(':'); string scenario=parts[0], profile=parts[1];
                bool ir=profile=="spatial";
                string user=scenario=="general" ? "我刚才唱的是第一段，暂时不用唱回来，也不用修改或保存素材。请用中文告诉我你理解了什么。" :
                    "请走到沙发旁边。用中文告诉我现在实际怎么样了。";
                string facts=scenario=="general" ? "本轮用户原话已最终确认，没有演唱播放、素材修改或记忆写入结果。" :
                    "空间决策已提交；本次只依据实际结果回复，不发出任何工具或动作。最新实际执行事实：移动失败，障碍物挡住路径；角色仍在原位置，没有到达沙发。没有当前图像，不要编造看到用户衣服、表情或沙发颜色。";
                // This synthetic fixture has no intervening history. Merge only
                // its adjacent leading systems for the current server template.
                // This is NOT a rewrite of production feedback/message ordering.
                string system=(string)profiles[profile];
                if(scenario=="general")system+="\n\n[已加载技能: singing]\n"+singing;
                system+="\n\n"+facts+"\n"+(string)profiles[ir?"streamContract":"legacyContract"];
                var messages=new JArray { new JObject{["role"]="system",["content"]=system} };
                messages.Add(new JObject{["role"]="user",["content"]=user});
                var payload=new JObject{["model"]="qwen3.6-35b-a3b",["stream"]=true,
                    ["stream_options"]=new JObject{["include_usage"]=true},["temperature"]=0,["max_tokens"]=1024,
                    ["enable_thinking"]=false,["chat_template_kwargs"]=new JObject{["enable_thinking"]=false},["messages"]=messages};
                if(ir)payload["response_format"]=RoleDecisionSpeechStream.ResponseFormat();
                string request=payload.ToString(Newtonsoft.Json.Formatting.None);
                var clock=Stopwatch.StartNew(); double? firstContent=null, firstSpeech=null;
                var raw=new StringBuilder(); string finish=null; var usage=new JObject();
                var channels=new RoleOutputChannels(part=>{ if(!firstSpeech.HasValue && !string.IsNullOrWhiteSpace(part.Text)) firstSpeech=clock.Elapsed.TotalSeconds; },
                    RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
                var parser=new RoleDecisionSpeechStream((segment,kind)=>channels.Push(segment), envelope: true);
                var row=new JObject{["scenario"]=scenario,["profile"]=profile,["requestBytes"]=Encoding.UTF8.GetByteCount(request),["sample"]=rows.Count};
                Console.WriteLine("Starting "+spec+" sample="+rows.Count);
                try
                {
                    using(var body=new StringContent(request,Encoding.UTF8,"application/json"))
                    using(var req=new HttpRequestMessage(HttpMethod.Post,"http://127.0.0.1:18080/v1/chat/completions"){Content=body})
                    using(var response=await client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            row["responseError"]=(await response.Content.ReadAsStringAsync());
                            response.EnsureSuccessStatusCode();
                        }
                        using(var stream=await response.Content.ReadAsStreamAsync())
                        using(var reader=new StreamReader(stream))
                        {
                            string line;
                            while((line=await reader.ReadLineAsync())!=null)
                            {
                                if(!line.StartsWith("data:"))continue;
                                string data=line.Substring(5).Trim(); if(data=="[DONE]")break;
                                var chunk=JObject.Parse(data);
                                if(chunk["usage"] is JObject u)usage=u;
                                foreach(var choice in chunk["choices"] as JArray ?? new JArray())
                                {
                                    string delta=(string)choice["delta"]?["content"];
                                    if(!string.IsNullOrEmpty(delta))
                                    {
                                        if(!firstContent.HasValue)firstContent=clock.Elapsed.TotalSeconds;
                                        raw.Append(delta); if(ir)parser.Push(delta);else channels.Push(delta);
                                    }
                                    if(choice["finish_reason"]?.Type==JTokenType.String)finish=(string)choice["finish_reason"];
                                }
                            }
                        }
                    }
                    bool valid=ir?parser.Finish(finish):finish=="stop";
                    channels.Finish(); valid &= !channels.HasInvalidPublicBoundary && !channels.HasInvalidSpeechPhase && !channels.HasMalformedTool && !channels.HasInvalidLanguage;
                    row["valid"]=valid;row["error"]=ir?parser.Error:null;
                    row["speech"]=channels.Speech;row["privateCharacters"]=channels.PrivateCharacters;
                    row["finishReason"]=finish;row["usage"]=usage;
                }
                catch(Exception ex){row["valid"]=false;row["error"]=ex.GetType().Name+": "+ex.Message;}
                row["firstContentSeconds"]=firstContent;row["firstPublicCandidateSeconds"]=firstSpeech;
                row["totalSeconds"]=clock.Elapsed.TotalSeconds;row["raw"]=raw.ToString();
                rows.Add(row);
                File.WriteAllText(Path.Combine(output,"results.json"),new JObject{["scope"]="Synthetic text-only inference with adjacent leading systems merged for server template compatibility; NOT the production message layout. No actions, TTS, Unity fact-review or actual microphone latency. First candidate is not audio onset. Shared server/cache state uncontrolled.",["samples"]=rows}.ToString());
                Console.WriteLine(new JObject{["scenario"]=scenario,["profile"]=profile,["valid"]=row["valid"],["firstContent"]=firstContent,["firstSpeech"]=firstSpeech,["total"]=row["totalSeconds"],["promptTokens"]=usage["prompt_tokens"]}.ToString(Newtonsoft.Json.Formatting.None));
            }
        }
    }
}
