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

    [Header("* 用 localhsot 會導致 UnityWebRequest 連不到 *")]
    [SerializeField]
    string _apiBaseUrl = "http://127.0.0.1:18763/pbapi/";

    [SerializeField]
    string[] _allowOrigins =
    {
        "http://127.0.0.1"
    };

    void OnEnable()
    {
        _server = new(_apiBaseUrl, _allowOrigins);
        _server.Start();
    }

    void OnDisable()
    {
        _server.Stop();
        _server.Dispose();
    }
}

public delegate ValueTask<string> JsonProtocolHandle(string json, IPEndPoint endPoint);

public sealed class ProtocolServer : IDisposable
{
    HttpListener _httpListener;
    Dictionary<string, JsonProtocolHandle> _jsonHandlers;
    IReadOnlyCollection<string> _allowOrigins;
    bool _disposed;

    public ProtocolServer(string apiBaseUrl, IReadOnlyCollection<string> allowOrigins)
    {
        _jsonHandlers = ProtocolHandleFromJsonBuilder.BuildHandleMappings(target: this, methodName: nameof(HandleAsync));
        _httpListener = new();
        _httpListener.Prefixes.Add(apiBaseUrl);
        _allowOrigins = new HashSet<string>(allowOrigins);
    }

    public async ValueTask<M2C_PlayerLogin> HandleAsync(C2M_PlayerLogin request, IPEndPoint endPoint)
    {
        Debug.LogFormat("Sever receive C2M_PlayerLogin: {0} from {1}", new { request.Account }, endPoint);
        await Task.Yield();

        Debug.Log("Server send M2C_PlayerLogin");
        return new M2C_PlayerLogin
        {
            SessionId = Guid.NewGuid().ToString("N"),
        };
    }

    public ValueTask<M2C_Echo> HandleAsync(C2M_Echo request, IPEndPoint endPoint)
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
        Application.runInBackground = true;
        try
        {
            while (_httpListener.IsListening)
            {
                var context = await _httpListener.GetContextAsync();
                var (request, response) = (context.Request, context.Response);

                var origin = request.Headers.GetValues("Origin")?.FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(origin) && _allowOrigins.Contains(origin))
                    response.AddHeader("Access-Control-Allow-Origin", origin);

                var path = request.Url.LocalPath.Split("/").Last();
                if (_jsonHandlers.TryGetValue(path, out var handler))
                {
                    using var reader = new StreamReader(request.InputStream);
                    var content = await reader.ReadToEndAsync();

                    var receiveJson = Uri.UnescapeDataString(content);
                    var sendJson = await handler(receiveJson, request.RemoteEndPoint);

                    response.ContentType = "application/json";
                    using var writer = new StreamWriter(response.OutputStream);
                    await writer.WriteLineAsync(sendJson);
                }
                else
                {
                    Debug.LogWarningFormat("Handle api reqest path: '{0}' not found", request.RawUrl);
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    response.Close();
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
            _jsonHandlers.Clear();
            _httpListener.Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public static class ProtocolHandleFromJsonBuilder
{
    static readonly MethodInfo JsonHandleMethod;

    static ProtocolHandleFromJsonBuilder()
    {
        var methodName = nameof(JsonHandleAsync);
        var flags = BindingFlags.Default;
        flags |= BindingFlags.Static;
        flags |= BindingFlags.NonPublic;
        JsonHandleMethod = typeof(ProtocolHandleFromJsonBuilder).GetMethod(methodName, flags);
    }

    public static Dictionary<string, JsonProtocolHandle> BuildHandleMappings(object target, string methodName)
    {
        var handleMethods = GetProtocolHandleMethods(target, methodName);

        var protocolBaseType = typeof(ProtocolBase);
        var endPointType = typeof(IPEndPoint);
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
                var genericArgs = baseType.GetGenericArguments();
                if (genericArgs.Length != 1)
                    return false;
                var responseType = genericArgs[0];
                if (typeof(ProtocolRequest<>).MakeGenericType(responseType) != baseType)
                    return false;
                return true;
            })
            .Where(t => !t.IsAbstract)
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .Where(handleMethods.ContainsKey)
            .ToDictionary(t => t.Name, requestType =>
            {
                // emit a delegate like:
                // (string json, IPEndPoint id) => JsonHandleAsync<C2M_A, M2C_A>(json, id, target.HandleAsync);
                var responseType = requestType.BaseType.GetGenericArguments()[0];
                var jsonArg = Expression.Parameter(typeof(string));
                var endPointIdArg = Expression.Parameter(endPointType);
                var protocolHandleMethod = handleMethods[requestType];
                var responseTaskType = protocolHandleMethod.ReturnType;
                var delegateType = Expression.GetFuncType(requestType, endPointType, responseTaskType);
                var handleDelegate = Delegate.CreateDelegate(delegateType, target, protocolHandleMethod);
                var delegateVar = Expression.Constant(handleDelegate);
                var typedJsonHandleMethod = JsonHandleMethod.MakeGenericMethod(requestType, responseType);
                var callJsonHandle = Expression.Call(typedJsonHandleMethod, jsonArg, endPointIdArg, delegateVar);
                var lambda = Expression.Lambda<JsonProtocolHandle>(callJsonHandle, jsonArg, endPointIdArg);
                return lambda.Compile();
            });
    }

    static Dictionary<Type, MethodInfo> GetProtocolHandleMethods(object target, string methodName)
    {
        var protocolBaseType = typeof(ProtocolBase);
        var endPointType = typeof(IPEndPoint);

        var flags = BindingFlags.Default;
        flags |= BindingFlags.Instance;
        flags |= BindingFlags.Public;
        return target.GetType().GetMethods(flags)
            .Where(method =>
            {
                if (!method.Name.Equals(methodName, StringComparison.Ordinal))
                    return false;
                var parameters = method.GetParameters();
                if (parameters.Length != 2)
                    return false;
                if (!protocolBaseType.IsAssignableFrom(parameters[0].ParameterType))
                    return false;
                if (!endPointType.IsAssignableFrom(parameters[1].ParameterType))
                    return false;
                var returnType = method.ReturnType;
                if (!returnType.IsGenericType)
                    return false;
                var returnProtocolType = returnType.GenericTypeArguments[0];
                if (!protocolBaseType.IsAssignableFrom(returnProtocolType))
                    return false;
                var requireReturnType = typeof(ValueTask<>).MakeGenericType(returnProtocolType);
                return returnType == requireReturnType;
            })
            .ToDictionary(m => m.GetParameters()[0].ParameterType);
    }

    static async ValueTask<string> JsonHandleAsync<TRequest, TResponse>(
        string requestJson,
        IPEndPoint endPoint,
        Func<TRequest, IPEndPoint, ValueTask<TResponse>> protocolHandle)
        where TRequest : ProtocolRequest<TResponse>, new()
        where TResponse : ProtocolResponse, new()
    {
        var request = JsonUtility.FromJson<TRequest>(requestJson);
        var response = await protocolHandle(request, endPoint);
        return JsonUtility.ToJson(response);
    }
}
