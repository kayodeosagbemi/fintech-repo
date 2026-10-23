namespace Plooto.Ap.Domain.Common;

/// <summary>
/// A business rule or state-machine violation raised by a domain entity (e.g. approving an already-paid invoice). Its
/// message is written for API callers, so the HTTP layer may surface it (409). Any other InvalidOperationException is an
/// infrastructure/framework failure and must never be echoed.
/// </summary>
public class DomainRuleException(string message) : InvalidOperationException(message);
