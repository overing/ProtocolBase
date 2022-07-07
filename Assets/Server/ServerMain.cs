using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

public sealed class ServerMain : MonoBehaviour
{
#if !UNITY_WEBGL || UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void AfterAssembliesLoaded() => DontDestroyOnLoad(new GameObject(nameof(ServerMain), typeof(ServerMain)));
#endif

    ProtocolServer _server;

    [SerializeField]
    string _apiBaseUrl = "http://127.0.0.1:18763/pbapi/";

    void OnEnable()
    {
        _server = new(_apiBaseUrl);
        _server.Start();
    }

    void OnDisable()
    {
        _server.Stop();
        _server.Dispose();
    }
}

public sealed class ProtocolServer : IDisposable
{
    HttpListener _httpListener;

    Dictionary<string, Func<string, IPEndPoint, ValueTask<string>>> _handlers;

    bool _disposed;

    public ProtocolServer(string apiBaseUrl)
    {
        _handlers = ProtocolCallbackUtility.BuildHandlers(target: this);
        _httpListener = new();
        _httpListener.Prefixes.Add(apiBaseUrl); // 用 localhsot 會導致 UnityWebRequest 連不到
    }

    async ValueTask<M2C_PlayerLogin> Handle(C2M_PlayerLogin request, IPEndPoint endPoint)
    {
        Debug.LogFormat("Sever receive C2M_PlayerLogin: {0} from {1}", new { request.Account }, endPoint);
        await Task.Yield();

        Debug.Log("Server send M2C_PlayerLogin");
        return new M2C_PlayerLogin
        {
            SessionId = Guid.NewGuid().ToString("N"),
        };
    }

    ValueTask<M2C_Echo> Handle(C2M_Echo request, IPEndPoint endPoint)
    {
        Debug.LogFormat("Sever receive C2M_Echo: {0} from {1}", new { request.UtcTicks }, endPoint);

        var nowTicks = DateTime.UtcNow.Ticks;

        Debug.Log("Server send M2C_Echo");
        return new ValueTask<M2C_Echo>(new M2C_Echo
        {
            C2MDelayTicks = nowTicks - request.UtcTicks,
            UtcTicks = nowTicks,
        });
    }

    public void Start()
    {
        _httpListener.Start();
        _ = ServeAsync();
    }

    async ValueTask ServeAsync()
    {
        Debug.LogFormat("Server start: {0}", string.Join(Environment.NewLine, _httpListener.Prefixes));
        try
        {
            while (_httpListener.IsListening)
            {
                var context = await _httpListener.GetContextAsync();

                var path = context.Request.Url.LocalPath.Split("/").Last();
                if (_handlers.TryGetValue(path, out var handler))
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var content = await reader.ReadToEndAsync();
                    var receiveJson = Uri.UnescapeDataString(content);
                    var sendJson = await handler(receiveJson, context.Request.RemoteEndPoint);

                    context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept, X-Access-Token, X-Application-Name, X-Request-Sent-Time");
                    context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONAL");
                    context.Response.AddHeader("Access-Control-Allow-Origin", "*");

                    using var writer = new StreamWriter(context.Response.OutputStream);
                    context.Response.ContentType = "application/json";
                    await writer.WriteLineAsync(sendJson);
                }
                else
                {
                    Debug.LogWarningFormat("Handle api reqest path: '{0}' not found", context.Request.RawUrl);
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                }
            }
            Debug.LogWarning("ServeAsync quit loop");
        }
        catch (ObjectDisposedException)
        {
            Debug.Log("Server close");
        }
        catch (Exception ex)
        {
            Debug.LogError(ex);
        }
    }

    public void Stop() => _httpListener.Stop();

    public void Dispose()
    {
        if (!_disposed)
        {
            _handlers.Clear();
            _httpListener.Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public static class ProtocolCallbackUtility
{
    public static Dictionary<string, Func<string, IPEndPoint, ValueTask<string>>> BuildHandlers(object target)
    {
        var protocolBaseType = typeof(ProtocolBase);
        var endPointType = typeof(IPEndPoint);

        var handleMethodFlags = BindingFlags.Default;
        handleMethodFlags |= BindingFlags.Instance;
        handleMethodFlags |= BindingFlags.Public;
        handleMethodFlags |= BindingFlags.NonPublic;
        var handlerMethods = target.GetType().GetMethods(handleMethodFlags)
            .Where(m =>
            {
                if (!m.Name.Equals("Handle", StringComparison.Ordinal))
                    return false;
                var parameters = m.GetParameters();
                if (parameters.Length != 2)
                    return false;
                if (!protocolBaseType.IsAssignableFrom(parameters[0].ParameterType))
                    return false;
                if (!endPointType.IsAssignableFrom(parameters[1].ParameterType))
                    return false;
                var returnType = m.ReturnType;
                if (!returnType.IsGenericType)
                    return false;
                var returnProtocolType = returnType.GenericTypeArguments[0];
                if (!protocolBaseType.IsAssignableFrom(returnProtocolType))
                    return false;
                var requireReturnType = typeof(ValueTask<>).MakeGenericType(returnProtocolType);
                return returnType == requireReturnType;
            })
            .ToDictionary(m => m.GetParameters()[0].ParameterType);

        var parseMethodFlags = BindingFlags.Default;
        parseMethodFlags |= BindingFlags.Static;
        parseMethodFlags |= BindingFlags.NonPublic;
        var parseMethod = typeof(ProtocolCallbackUtility).GetMethod(nameof(ParseTask), parseMethodFlags) ?? throw new MissingMethodException();

        return protocolBaseType.Assembly.GetTypes()
            .Where(t =>
            {
                if (!protocolBaseType.IsAssignableFrom(t))
                    return false;
                if (t.IsAbstract)
                    return false;
                if (t.GetConstructor(Type.EmptyTypes) == null)
                    return false;
                var baseType = t.BaseType;
                if (!baseType.IsGenericType)
                    return false;
                var gps = baseType.GetGenericArguments();
                if (gps.Length != 1)
                    return false;
                var responseType = gps[0];
                if (typeof(ProtocolRequest<>).MakeGenericType(responseType) != baseType)
                    return false;
                return true;
            })
            .Where(protocolBaseType.IsAssignableFrom)
            .Where(t => !t.IsAbstract)
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .Where(handlerMethods.ContainsKey)
            .ToDictionary(t => t.Name, t =>
            {
                var handleMethod = handlerMethods[t];
                var responseType = t.BaseType.GetGenericArguments()[0];
                var requestArg = Expression.Parameter(protocolBaseType);
                var typedRequest = Expression.Convert(requestArg, t);
                var endPointArg = Expression.Parameter(endPointType);
                var callHandle = Expression.Call(Expression.Constant(target), handleMethod, typedRequest, endPointArg);
                var typedParseTask = parseMethod.MakeGenericMethod(responseType);
                var parseTask = Expression.Call(typedParseTask, callHandle);
                var lambda = Expression.Lambda<Func<ProtocolBase, IPEndPoint, ValueTask<string>>>(parseTask, requestArg, endPointArg);
                var @delegate = lambda.Compile();
                return new Func<string, IPEndPoint, ValueTask<string>>(async (json, endPoint) =>
                {
                    var request = (ProtocolBase)JsonUtility.FromJson(json, t);
                    return await @delegate(request, endPoint);
                });
            });
    }

    static async ValueTask<string> ParseTask<TResponse>(ValueTask<TResponse> task) where TResponse : ProtocolResponse
    {
        var response = await task;
        return JsonUtility.ToJson(response);
    }
}
