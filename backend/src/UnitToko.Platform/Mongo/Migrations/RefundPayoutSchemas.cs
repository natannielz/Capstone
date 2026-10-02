using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using B = UnitToko.Platform.Billing.RefundPayoutBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class RefundPayoutSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Beneficiaries, B.Drafts, B.Payouts];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Version(long minimum = 0) => S.Number(minimum, long.MaxValue - 1);
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Money => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(.01m) },
        { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument People(int minimum = 1, int maximum = 512) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", ReceivingAccountSchemas.Person() } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    internal static BsonDocument Party() => PaymentVerificationSchemas.Source()["properties"]["party"].DeepClone().AsBsonDocument;
    internal static BsonDocument Book() => S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) }, { "currency", S.Choice("IDR") } });
    internal static BsonDocument PolicyFields() {
        var fields = Header(); fields.Add("revision", Version(1)); fields.Add("scope", ReceivingAccountSchemas.Scope()); fields.Add("rule", S.Choice(RefundPayoutPolicy.ExactReservedRefund));
        fields.Add("payingAccountId", S.Number(1)); fields.Add("payingAccountVersionId", S.Number(1));
        foreach (var field in new[] { "beneficiaryVerificationProcedureReference", "payoutRecordingProcedureReference", "operatingOwnerReference", "approvalReference" }) fields.Add(field, S.Text(256));
        fields.Add("active", new BsonDocument("bsonType", "bool")); fields.Add("approvedAt", Date); fields.Add("expiresAt", Date); return fields;
    }
    internal static BsonDocument BeneficiaryFields() {
        var fields = Header(); fields.Add("revision", Version(1)); fields.Add("scope", ReceivingAccountSchemas.Scope()); fields.Add("buyingOrganizationId", S.Number(1));
        fields.Add("details", ReceivingAccountSchemas.Details()); fields.Add("physicalIdentity", Hash); fields.Add("method", S.Choice("bank-contact", "authenticated-bank-record"));
        foreach (var field in new[] { "authorizationReference", "trustedSourceReference", "trustedChannelReference", "operatingOwnerReference", "approvalReference" }) fields.Add(field, S.Text(256));
        foreach (var field in new[] { "proposedBy", "verifiedBy", "approvedBy" }) fields.Add(field, S.Number(1));
        fields.Add("people", People(2, 3)); fields.Add("active", new BsonDocument("bsonType", "bool")); fields.Add("approvedAt", Date); fields.Add("expiresAt", Date); fields.Add("snapshotHash", Hash); return fields;
    }
    internal static BsonDocument Approval() => S.Object(new() { { "id", S.Number(1) }, { "hash", Hash }, { "caseId", S.Number(1) }, { "returnId", S.Number(1) }, { "invoiceId", S.Number(1) },
        { "party", Party() }, { "reservedRefund", Money }, { "approvedBy", S.Number(1) }, { "approvedByPersonId", S.Number(1) }, { "recordedAt", Date },
        { "journalId", S.Number(1) }, { "journalFingerprint", Hash }, { "refundLiabilityAccountId", S.Number(1) }, { "refundLiabilityAccountCode", S.Text(80) } });
    internal static BsonDocument PayingBank() => S.Object(new() { { "accountId", S.Number(1) }, { "versionId", S.Number(1) }, { "headHash", Hash }, { "versionHash", Hash }, { "physicalIdentity", Hash },
        { "identityNamespace", S.Text(80) }, { "anchorHash", Hash }, { "firstReceiptId", S.Number(1) }, { "firstReceiptHash", Hash }, { "cashJournalId", S.Number(1) }, { "cashJournalFingerprint", Hash },
        { "scope", ReceivingAccountSchemas.Scope() }, { "book", Book() }, { "cashAccountId", S.Number(1) }, { "cashAccountCode", S.Text(80) }, { "currency", S.Choice("IDR") } });
    internal static BsonDocument Source() => S.Object(new() { { "approval", Approval() }, { "beneficiary", S.Object(BeneficiaryFields()) }, { "payingBank", PayingBank() }, { "policy", S.Object(PolicyFields()) } });
    internal static BsonDocument Input() => S.Object(new() { { "beneficiaryId", S.Number(1) }, { "expectedBeneficiaryRevision", Version(1) }, { "payingAccountId", S.Number(1) }, { "payingAccountVersionId", S.Number(1) },
        { "amount", Money }, { "transferDate", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z" } } }, { "transferReference", S.Text(160) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new() { { "scope.fulfillmentOrganizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 }, { "scope.warehouseId", 1 } }],
        B.Beneficiaries => [new() { { "buyingOrganizationId", 1 }, { "scope.fulfillmentOrganizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 }, { "scope.warehouseId", 1 } }],
        B.Drafts => [new() { { "userId", 1 }, { "requestKey", 1 } }],
        B.Payouts => [new("financialApprovalId", 1), new("draftId", 1), new() { { "actorId", 1 }, { "requestKey", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    private static BsonDocument Op(string op, params BsonValue[] values) => new(op, new BsonArray(values));
    private static BsonDocument VerifiedPeople(string path) {
        var ids = new BsonDocument("$map", new BsonDocument { { "input", "$" + path }, { "as", "person" }, { "in", "$$person.userId" } });
        return Op("$and", Op("$eq", new BsonDocument("$size", ids), new BsonDocument("$size", Op("$setUnion", ids, new BsonArray()))),
            Op("$eq", ids, new BsonDocument("$sortArray", new BsonDocument { { "input", ids }, { "sortBy", 1 } })),
            new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument { { "input", "$" + path }, { "as", "person" },
                { "in", Op("$and", Op("$eq", "$$person.verification", "Verified"), Op("$gt", "$$person.personId", 0L), Op("$gt", "$$person.version", 0L)) } }) }));
    }
    private static BsonDocument ExactPerson(string path, string user, string person) => Op("$eq", new BsonDocument("$size", new BsonDocument("$filter", new BsonDocument {
        { "input", "$" + path }, { "as", "entry" }, { "cond", Op("$and", Op("$eq", "$$entry.userId", "$" + user), Op("$eq", "$$entry.personId", "$" + person)) } })), 1);
    private static void BeneficiaryRules(BsonArray expressions, string prefix) {
        string Field(string name) => "$" + prefix + name;
        expressions.Add(Op("$gt", Field("expiresAt"), Field("approvedAt"))); expressions.Add(Op("$ne", Field("proposedBy"), Field("verifiedBy"))); expressions.Add(Op("$ne", Field("proposedBy"), Field("approvedBy")));
        expressions.Add(VerifiedPeople(prefix + "people"));
        expressions.Add(Op("$eq", new BsonDocument("$map", new BsonDocument { { "input", Field("people") }, { "as", "person" }, { "in", "$$person.userId" } }),
            new BsonDocument("$sortArray", new BsonDocument { { "input", Op("$setUnion", new BsonArray { Field("proposedBy"), Field("verifiedBy"), Field("approvedBy") }, new BsonArray()) }, { "sortBy", 1 } })));
        expressions.Add(BsonDocument.Parse("""
            { "$let": { "vars": {
              "maker": { "$arrayElemAt": [{ "$filter": { "input": "$PEOPLE", "as": "p", "cond": { "$eq": ["$$p.userId", "$MAKER"] } } }, 0] },
              "checker": { "$arrayElemAt": [{ "$filter": { "input": "$PEOPLE", "as": "p", "cond": { "$eq": ["$$p.userId", "$CHECKER"] } } }, 0] },
              "approver": { "$arrayElemAt": [{ "$filter": { "input": "$PEOPLE", "as": "p", "cond": { "$eq": ["$$p.userId", "$APPROVER"] } } }, 0] }
            }, "in": { "$and": [{ "$ne": ["$$maker.personId", "$$checker.personId"] }, { "$ne": ["$$maker.personId", "$$approver.personId"] }] } } }
            """.Replace("PEOPLE", prefix + "people", StringComparison.Ordinal).Replace("MAKER", prefix + "proposedBy", StringComparison.Ordinal)
                .Replace("CHECKER", prefix + "verifiedBy", StringComparison.Ordinal).Replace("APPROVER", prefix + "approvedBy", StringComparison.Ordinal)));
    }
    internal static BsonDocument Validator(string name) {
        var fields = Header(); var expressions = new BsonArray(); var terms = new BsonArray();
        if (name == B.Policies || name == B.Beneficiaries) {
            fields = name == B.Policies ? PolicyFields() : BeneficiaryFields(); fields.Add("fence", Version());
            foreach (var field in new[] { "approvedAt", "expiresAt" }) terms.Add(S.DateRange(field));
            expressions.Add(Op("$gt", "$expiresAt", "$approvedAt"));
            if (name == B.Beneficiaries) BeneficiaryRules(expressions, "");
        } else if (name == B.Drafts || name == B.Payouts) {
            var draft = name == B.Drafts; var at = draft ? "createdAt" : "recordedAt"; var actor = draft ? "userId" : "actorId";
            fields.AddRange(new BsonDocument { { "operation", S.Choice(draft ? B.DraftOperation : B.Operation) }, { "financialApprovalId", S.Number(1) }, { "approvalHash", Hash },
                { "caseId", S.Number(1) }, { "returnId", S.Number(1) }, { "invoiceId", S.Number(1) }, { "party", Party() }, { "scope", ReceivingAccountSchemas.Scope() }, { "scopes", PaymentDraftSchemas.Scopes() },
                { "source", Source() }, { "input", Input() }, { "binding", Hash }, { "people", People(2) }, { "actorGrantVersion", S.Number(1) },
                { at, Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash }, { "fence", Version() } }); S.Metadata(fields);
            if (draft) fields.AddRange(new BsonDocument { { "revision", S.Number(1, 1) }, { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "recordedPayoutId", Nullable(S.Number(1)) } });
            else fields.AddRange(new BsonDocument { { "state", S.Choice("recorded_unverified") }, { "draftId", S.Number(1) }, { "draftHash", Hash }, { "draftRevision", S.Number(1, 1) },
                { "evidence", PaymentSubmissionSchemas.Reference() }, { "reservationId", S.Number(1) }, { "pinId", S.Number(1) }, { "recordedBy", S.Number(1) }, { "actorId", S.Number(1) } });
            foreach (var field in new[] { at, "source.approval.recordedAt", "source.policy.approvedAt", "source.policy.expiresAt", "source.beneficiary.approvedAt", "source.beneficiary.expiresAt" }) terms.Add(S.DateRange(field));
            foreach (var (left, right) in new[] { ("financialApprovalId", "source.approval.id"), ("approvalHash", "source.approval.hash"), ("caseId", "source.approval.caseId"),
                ("returnId", "source.approval.returnId"), ("invoiceId", "source.approval.invoiceId"), ("party", "source.approval.party"), ("scope", "source.policy.scope"),
                ("scope", "source.beneficiary.scope"), ("scope", "source.payingBank.scope"), ("party.buyingOrganizationId", "source.beneficiary.buyingOrganizationId"),
                ("input.beneficiaryId", "source.beneficiary._id"), ("input.expectedBeneficiaryRevision", "source.beneficiary.revision"),
                ("input.payingAccountId", "source.payingBank.accountId"), ("input.payingAccountVersionId", "source.payingBank.versionId"), ("input.amount", "source.approval.reservedRefund"),
                ("input.payingAccountId", "source.policy.payingAccountId"), ("input.payingAccountVersionId", "source.policy.payingAccountVersionId"),
                ("party.fulfillmentOrganizationId", "source.payingBank.book.organizationId"), ("party.shopId", "source.payingBank.book.shopId"), ("party.channelId", "source.payingBank.book.channelId"),
                ("scope.fulfillmentOrganizationId", "party.fulfillmentOrganizationId"), ("scope.shopId", "party.shopId"), ("scope.channelId", "party.channelId") }) expressions.Add(Op("$eq", "$" + left, "$" + right));
            foreach (var prefix in new[] { "source.policy.", "source.beneficiary." }) {
                expressions.Add(Op("$eq", "$" + prefix + "active", true)); expressions.Add(Op("$gte", "$" + at, "$" + prefix + "approvedAt")); expressions.Add(Op("$lt", "$" + at, "$" + prefix + "expiresAt"));
            }
            expressions.Add(Op("$gte", "$" + at, "$source.approval.recordedAt")); expressions.Add(Op("$ne", "$" + actor, "$source.approval.approvedBy"));
            expressions.Add(VerifiedPeople("people")); expressions.Add(ExactPerson("people", "source.approval.approvedBy", "source.approval.approvedByPersonId"));
            if (draft) { expressions.Add(ExactPerson("people", "userId", "personId")); expressions.Add(Op("$ne", "$personId", "$source.approval.approvedByPersonId")); }
            else {
                expressions.Add(Op("$eq", "$recordedBy", "$actorId"));
                expressions.Add(Op("$eq", new BsonDocument("$size", new BsonDocument("$filter", new BsonDocument { { "input", "$people" }, { "as", "person" },
                    { "cond", Op("$and", Op("$eq", "$$person.userId", "$actorId"), Op("$ne", "$$person.personId", "$source.approval.approvedByPersonId")) } })), 1));
            }
            expressions.Add(Op("$lte", "$input.transferDate", new BsonDocument("$dateToString", new BsonDocument { { "date", "$" + at }, { "format", "%Y-%m-%d" }, { "timezone", "Asia/Jakarta" } })));
            BeneficiaryRules(expressions, "source.beneficiary.");
        } else throw new ArgumentOutOfRangeException(nameof(name));
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields))); if (expressions.Count > 0) terms.Add(new BsonDocument("$expr", Op("$and", expressions.ToArray())));
        return new("$and", terms);
    }
}
