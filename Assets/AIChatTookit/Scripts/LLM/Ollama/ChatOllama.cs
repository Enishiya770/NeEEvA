// 仍然在同一个 ChatOllama.cs 文件中
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class ChatOllama : LLM
{
    public string m_SystemSetting = string.Empty;
    public ModelType m_GptModel = ModelType.neural_chat;

    private void Start()
    {
        m_DataList.Add(new SendData("system", m_SystemSetting));
    }

    public override void PostMsg(string _msg, Action<string> _callback)
    {
        base.PostMsg(_msg, _callback);
    }

    public override IEnumerator Request(string _postWord, System.Action<string> _callback)
    {
        stopwatch.Restart();
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            PostData _postData = new PostData
            {
                model = m_GptModel.ToModelString(),  // ✅ 调用扩展方法
                messages = m_DataList
            };

            string _jsonText = JsonUtility.ToJson(_postData);
            byte[] data = System.Text.Encoding.UTF8.GetBytes(_jsonText);
            request.uploadHandler = new UploadHandlerRaw(data);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.responseCode == 200)
            {
                string _msgBack = request.downloadHandler.text;
                MessageBack _textback = JsonUtility.FromJson<MessageBack>(_msgBack);
                if (_textback != null && _textback.message != null)
                {
                    string _backMsg = _textback.message.content;
                    m_DataList.Add(new SendData("assistant", _backMsg));
                    _callback(_backMsg);
                }
            }
            else
            {
                Debug.LogError(request.downloadHandler.text);
            }

            stopwatch.Stop();
            Debug.Log("Ollama耗时：" + stopwatch.Elapsed.TotalSeconds);
        }
    }

    #region 数据定义

    public enum ModelType
    {
        neural_chat,
        deepseek_r1,
        qwen_8b
    }

    [Serializable]
    public class PostData
    {
        public string model;
        public List<SendData> messages;
        public bool stream = false;
    }

    [Serializable]
    public class MessageBack
    {
        public string created_at;
        public string model;
        public Message message;
    }

    [Serializable]
    public class Message
    {
        public string role;
        public string content;
    }

    #endregion
}

// ✅ 顶层静态类，不能嵌套在 ChatOllama 里面
public static class ModelTypeExtensions
{
    public static string ToModelString(this ChatOllama.ModelType model)
    {
        return model switch
        {
            ChatOllama.ModelType.neural_chat => "neural-chat",
            ChatOllama.ModelType.deepseek_r1 => "deepseek-r1",
            ChatOllama.ModelType.qwen_8b => "qwen3:8b",
            _ => "llama3"
        };
    }
}
