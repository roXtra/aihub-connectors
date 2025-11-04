using AiHub.Connector.Roxtra;

namespace AiHub.Connector.ExternalConnectors;

public interface IExternalConnector
{
	Task HandleKnowledgePoolFileAddedAsync(string knowledgePoolId, RoxFile file, CancellationToken cancellationToken = default);
	Task HandleKnowledgePoolFileRemovedAsync(string knowledgePoolId, string roxFileId, CancellationToken cancellationToken = default);
	Task HandleKnowledgePoolCreatedAsync(string knowledgePoolId, CancellationToken cancellationToken = default);
	Task HandleKnowledgePoolMemberAddedAsync(
		string knowledgePoolId,
		Guid roxtraGroupGid,
		string externalGroupId,
		CancellationToken cancellationToken = default
	);
	Task HandleKnowledgePoolMemberRemovedAsync(
		string knowledgePoolId,
		Guid roxtraGroupGid,
		string externalGroupId,
		CancellationToken cancellationToken = default
	);
	Task HandleKnowledgePoolRemovedAsync(string knowledgePoolId, CancellationToken cancellationToken = default);
	Task HandleFileUpdatedAsync(RoxFile file, CancellationToken cancellationToken = default);
	Task InitializeAsync(CancellationToken cancellationToken = default);
}
