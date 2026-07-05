using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class ChatZhipu : LLM
{

    #region 参数
    /// <summary>
    /// 选择的模型
    /// </summary>
    [SerializeField] public string m_ChatModelName = "glm-4-flash-250414";
    /// <summary>
    /// 调用方式  invoke/async-invoke/sse-invoke  先实现同步模式
    /// </summary>
    [SerializeField] private string m_InvokeMethod = "invoke";
    /// <summary>
    /// AI设定
    /// </summary>
    public string m_SystemSetting = string.Empty;
    /// <summary>
    /// 智普AI的apikey
    /// </summary>
    [Header("填写智普AI的apikey")]
    [SerializeField] private string m_Key = string.Empty;
    //api key
    private string m_ApiKey = string.Empty;
    //secret key
    private string m_SecretKey = string.Empty;
    #endregion

    private void Awake()
    {
        OnInit();
    }
    /// <summary>
    /// 初始化
    /// </summary>
    private void OnInit()
    {
        //运行时，添加AI设定
        m_DataList.Add(new SendData("system", m_SystemSetting));
        url = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
        SplitKey();
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    /// <returns></returns>
    public override void PostMsg(string _msg, Action<string> _callback)
    {
        base.PostMsg(_msg, _callback);
    }


    /// <summary>
    /// 发送数据
    /// </summary> 
    /// <param name="_postWord"></param>
    /// <param name="_callback"></param>
    /// <returns></returns>
    public override IEnumerator Request(string _postWord, System.Action<string> _callback)
    {
        stopwatch.Start();
        string jsonPayload = JsonConvert.SerializeObject(new PostData
        {
            model = m_ChatModelName,
            messages = m_DataList
        });

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = (UploadHandler)new UploadHandlerRaw(data);
            request.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", GetToken());

            yield return request.SendWebRequest();

            if (request.responseCode == 200)
            {
                string _msgBack = request.downloadHandler.text;
                MessageBack _textback = JsonUtility.FromJson<MessageBack>(_msgBack);
                if (_textback != null && _textback.choices.Count > 0)
                {

                    string _backMsg = _textback.choices[0].message.content;
                    //添加记录
                    m_DataList.Add(new SendData("assistant", _backMsg));
                    _callback(_backMsg);
                }
            }
            else
            {
                string _msgBack = request.downloadHandler.text;
                Debug.LogError(_msgBack);
            }

        }

        stopwatch.Stop();
        Debug.Log("智普AI-耗时：" + stopwatch.Elapsed.TotalSeconds);
    }





    /// <summary>
    /// 处理key
    /// </summary>
    private void SplitKey()
    {
        try {
            if (m_Key == "")
                return;

            string[] _split = m_Key.Split('.');
            m_ApiKey = _split[0];
            m_SecretKey = _split[1];
        } 
        catch { }


    }

    #region 生成api鉴权token

    /// <summary>
    /// 生成api鉴权 token
    /// </summary>
    /// <returns></returns>
    private string GetToken()
    {
        long expirationMilliseconds = DateTimeOffset.Now.AddHours(1).ToUnixTimeMilliseconds();
        long timestampMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        string jwtToken = GenerateJwtToken(m_ApiKey, expirationMilliseconds, timestampMilliseconds);
        return jwtToken;
    }
    //获取token
    private string GenerateJwtToken(string apiKeyId, long expirationMilliseconds, long timestampMilliseconds)
    {
        // 构建Header
        string _headerJson = "{\"alg\":\"HS256\",\"sign_type\":\"SIGN\"}";

        string encodedHeader = Base64UrlEncode(_headerJson);

        // 构建Payload
        string _playLoadJson = string.Format("{{\"api_key\":\"{0}\",\"exp\":{1}, \"timestamp\":{2}}}", apiKeyId, expirationMilliseconds, timestampMilliseconds);

        string encodedPayload = Base64UrlEncode(_playLoadJson);

        // 构建签名
        string signature = HMACsha256(m_SecretKey, $"{encodedHeader}.{encodedPayload}");
        // 组合Header、Payload和Signature生成JWT令牌
        string jwtToken = $"{encodedHeader}.{encodedPayload}.{signature}";

        return jwtToken;
    }
    // Base64 URL编码
    private string Base64UrlEncode(string input)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        string base64 = Convert.ToBase64String(inputBytes);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
    // 使用HMAC SHA256生成签名
    private string HMACsha256(string apiSecretIsKey, string buider)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(apiSecretIsKey);
        HMACSHA256 hMACSHA256 = new System.Security.Cryptography.HMACSHA256(bytes);
        byte[] date = Encoding.UTF8.GetBytes(buider);
        date = hMACSHA256.ComputeHash(date);
        hMACSHA256.Clear();

        return Convert.ToBase64String(date);

    }
    #endregion



    #region 数据定义
    [Serializable]
    public class PostData
    {
        public string model;
        public List<SendData> messages;
    }


    [Serializable]
    public class MessageBack
    {
        public string id;
        public string created;
        public string model;
        public List<MessageBody> choices;
    }
    [Serializable]
    public class MessageBody
    {
        public Message message;
        public string finish_reason;
        public string index;
    }
    [Serializable]
    public class Message
    {
        public string role;
        public string content;
    }


    #endregion

}
