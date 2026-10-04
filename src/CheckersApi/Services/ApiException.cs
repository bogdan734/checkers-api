namespace CheckersApi.Services;

public sealed class ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public ApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public static ApiException Unprocessable(string code, string message) => new(422, code, message);
}
