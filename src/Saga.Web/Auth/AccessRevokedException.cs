namespace Saga.Web.Auth;

/// <summary>Thrown when a signed-in principal maps to a soft-deleted (removed) user.</summary>
public class AccessRevokedException(string message) : Exception(message);
