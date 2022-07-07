using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public sealed class ClientMain : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AfterSceneLoad() => new GameObject(nameof(ClientMain), typeof(ClientMain));

    ProtocolClient _client = new ProtocolClient();

    const int MessageQueueSize = 63;

    string _apiBaseUrl = "http://127.0.0.1:18763/pbapi/";

    Queue<string> _messages = new Queue<string>();

    Vector2 _viewPort;

    void OnGUI()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(Screen.width), GUILayout.Height(Screen.height));

        GUILayout.Label("API Base Url:");

        _apiBaseUrl = GUILayout.TextField(_apiBaseUrl, GUILayout.ExpandWidth(expand: true));

        if (GUILayout.Button("Send C2M_PlayerLogin", GUILayout.ExpandWidth(expand: true)))
            _ = LoginAsync();

        if (GUILayout.Button("Send C2M_Echo", GUILayout.ExpandWidth(expand: true)))
            _ = EchoAsync();

        _viewPort = GUILayout.BeginScrollView(_viewPort, GUI.skin.box, GUILayout.ExpandWidth(expand: true), GUILayout.ExpandHeight(expand: true));

        foreach (var message in _messages)
            GUILayout.Label(message);

        GUILayout.EndScrollView();

        GUILayout.EndVertical();
    }

    void EnqueueMessage(string format, params object[] arguments) => EnqueueMessage(string.Format(format, arguments));

    void EnqueueMessage(string message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > MessageQueueSize)
            _messages.Dequeue();
        _viewPort.y = float.MaxValue;
    }

    async Task LoginAsync()
    {
        try
        {
            EnqueueMessage("Client send C2M_PlayerLogin");
            var response = await _client.RequestAsync<C2M_PlayerLogin, M2C_PlayerLogin>(_apiBaseUrl, new()
            {
                Account = "overing",
            });
            EnqueueMessage("Client receive M2C_PlayerLogin: {0}", new { response.SessionId });
        }
        catch (Exception ex)
        {
            EnqueueMessage("Error: {0}", ex.Message);
        }
    }

    async Task EchoAsync()
    {
        try
        {
            EnqueueMessage("Client send C2M_Echo");
            var beginTicks = DateTime.UtcNow.Ticks;
            var response = await _client.RequestAsync<C2M_Echo, M2C_Echo>(_apiBaseUrl, new()
            {
                UtcTicks = beginTicks,
            });
            var endTicks = DateTime.UtcNow.Ticks;
            var M2CDelayTicks = endTicks - response.UtcTicks;
            EnqueueMessage("Client receive M2C_Echo: {0}", new
            {
                response.UtcTicks,
                response.C2MDelayTicks,
                M2CDelayTicks,
                (TimeSpan.FromTicks(response.C2MDelayTicks) + TimeSpan.FromTicks(M2CDelayTicks)).TotalSeconds,
            });
        }
        catch (Exception ex)
        {
            EnqueueMessage("Error: {0}", ex.Message);
        }
    }
}

public sealed class ProtocolClient
{
    public async Task<TResponse> RequestAsync<TRequest, TResponse>(string apiBaseUrl, TRequest request)
        where TRequest : ProtocolRequest<TResponse>, new()
        where TResponse : ProtocolResponse, new()
    {
        var sendJson = JsonUtility.ToJson(request);
        var path = typeof(TRequest).Name;
        var receiveJson = await JsonWebRequestAsync(apiBaseUrl + path, sendJson);
        return JsonUtility.FromJson<TResponse>(receiveJson);
    }

    async Task<string> JsonWebRequestAsync(string url, string json)
    {
        var request = UnityWebRequest.Post(url, json);
        request.timeout = 3;
        var operation = request.SendWebRequest();
        while (request.result == UnityWebRequest.Result.InProgress)
            await Task.Yield();
        if (request.result == UnityWebRequest.Result.Success)
            return request.downloadHandler.text;
        throw new Exception($"{request.error} <- '{request.url}'");
    }
}
