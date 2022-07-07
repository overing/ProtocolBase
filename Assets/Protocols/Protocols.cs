
public abstract class ProtocolBase { }

public abstract class ProtocolResponse : ProtocolBase { }

public abstract class ProtocolRequest<TResponse> : ProtocolBase where TResponse : ProtocolResponse, new() { }

public sealed class M2C_PlayerLogin : ProtocolResponse
{
    public string SessionId;
}

public sealed class C2M_PlayerLogin : ProtocolRequest<M2C_PlayerLogin>
{
    public string Account;
}

public sealed class M2C_Echo : ProtocolResponse
{
    public long UtcTicks;
    public long C2MDelayTicks;
}

public sealed class C2M_Echo : ProtocolRequest<M2C_Echo>
{
    public long UtcTicks;
}
