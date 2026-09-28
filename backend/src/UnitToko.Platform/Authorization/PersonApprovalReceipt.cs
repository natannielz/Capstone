using MongoDB.Bson;
using UnitToko.Modules.Identity;

namespace UnitToko.Platform.Authorization;

/// <summary>Private receipt metadata keeps the original server-loaded participants for a committed command.
/// It is not a person proof: every replay resolves and fences current authority again.</summary>
internal static class PersonApprovalReceipt
{
    internal const string Field = "personApproval";
    private static readonly string[] ApprovalFields =
    [
        "revision", "expectedRevision", "requestedBy", "recordedBy", "approvedBy", "submittedBy",
        "closedBy", "countedBy", "proposedBy", "beneficiaryId", "previousRevision"
    ];

    internal static BsonDocument Create(string permission, ApprovalRequest approval) => new()
    {
        { "schemaVersion", 1 }, { "permission", permission },
        { "approval", new BsonDocument
            {
                { "revision", approval.Revision }, { "expectedRevision", approval.ExpectedRevision },
                { "requestedBy", Value(approval.RequestedBy) }, { "recordedBy", Value(approval.RecordedBy) },
                { "approvedBy", Value(approval.ApprovedBy) }, { "submittedBy", Value(approval.SubmittedBy) },
                { "closedBy", Value(approval.ClosedBy) }, { "countedBy", Value(approval.CountedBy) },
                { "proposedBy", Value(approval.ProposedBy) }, { "beneficiaryId", Value(approval.BeneficiaryId) },
                { "previousRevision", Value(approval.PreviousRevision) }
            }
        }
    };

    internal static ApprovalRequest Read(BsonDocument receipt, string permission)
    {
        if (!receipt.TryGetValue(Field, out var raw) || !raw.IsBsonDocument) throw Invalid();
        var row = raw.AsBsonDocument;
        if (row.ElementCount != 3 || !row.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !row.TryGetValue("permission", out var storedPermission) || !storedPermission.IsString || storedPermission.AsString != permission ||
            !row.TryGetValue("approval", out var approvalRaw) || !approvalRaw.IsBsonDocument) throw Invalid();
        var approval = approvalRaw.AsBsonDocument;
        if (approval.ElementCount != ApprovalFields.Length || ApprovalFields.Any(field => !approval.Contains(field)) ||
            approval.Any(field => !field.Value.IsBsonNull && (!field.Value.IsInt64 || field.Value.AsInt64 <= 0)) ||
            !approval["revision"].IsInt64 || !approval["expectedRevision"].IsInt64) throw Invalid();
        long? Optional(string name) => approval[name].IsBsonNull ? null : approval[name].AsInt64;
        return new(approval["revision"].AsInt64, approval["expectedRevision"].AsInt64,
            Optional("requestedBy"), Optional("recordedBy"), Optional("approvedBy"), Optional("submittedBy"),
            Optional("closedBy"), Optional("countedBy"), Optional("proposedBy"), Optional("beneficiaryId"), Optional("previousRevision"));
    }

    private static BsonValue Value(long? value) => value is { } id ? new BsonInt64(id) : BsonNull.Value;
    private static AuthorizationDeniedException Invalid() => new("person_receipt_invalid");
}
