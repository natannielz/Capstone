// Generated from backend/contracts/openapi.json. Do not edit manually.
// Regenerate: npm run generate --prefix backend/contracts
export interface paths {
    "/api/v1/access/catalog": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["AccessCatalog"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/current": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["CurrentAccess"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/grants/{id}/revoke": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RevokeAccessGrant"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/grants/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewAccessGrant"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/grants/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeAccessGrant"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/grants/proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveAccessGrant"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/historical-grants/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewHistoricalAccessGrant"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/historical-grants/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeHistoricalAccessGrant"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/historical-targets/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["QueryHistoricalAccessTargets"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/history/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["AccessHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/memberships/{id}/suspend": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SuspendMembership"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/memberships/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeMembership"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/memberships/proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveMembership"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/operations/lookup": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["LookupAccessOperation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/operations/resolve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ResolveAccessOperation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["AccessProposals"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/proposals/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectAccessProposal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/proposals/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawAccessProposal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/scopes/{id}/suspend": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SuspendAccessScope"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/subjects/{id}/suspend": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SuspendIdentity"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/access/subjects/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["AccessSubject"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/assortments/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogAssortmentQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/categories": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogCategorySave"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/categories/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogCategoryQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/families": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogFamilyCreate"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/families/{id}/edit": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogFamilyEdit"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/families/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogFamilyRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/families/{id}/skus": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogSkuCreate"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/families/{id}/skus/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogSkuQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/families/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogFamilyQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogReceiptQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/owners/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogOwnerOptions"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/skus/{id}/definition": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogDefinitionRevise"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/skus/{id}/publications": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogSkuPublish"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/skus/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogSkuRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/skus/{id}/replacement": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogSkuReplace"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/administration/skus/{id}/retire": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogSkuRetire"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/variants/{publicId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogPublishedVariantRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/variants/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogPublishedVariantQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/directory/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationDirectoryQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/directory/search": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationDirectorySearch"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/invitations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationProposeInvitation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/invitations/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationApproveInvitation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/invitations/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationRejectInvitation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/invitations/{id}/revoke": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationRevokeInvitation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/invitations/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationWithdrawInvitation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/invitations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationInvitationsQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewReceiptQuery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/operations/resolve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewReceiptResolve"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewPropose"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewApprove"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals/{id}/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewApprovalPreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewDetail"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewReject"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewWithdraw"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/proposals/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewList"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/targets/{id}/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewProposalPreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/person-reviews/targets/search": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PersonReviewTargetSearch"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/administration/recovery": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityAdministrationRequestRecovery"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/csrf": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["IdentityCsrf"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/provider-events/auth0-events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["Auth0SecurityEvents"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/provider-events/auth0-logs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["Auth0SecurityLogs"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/identity/session": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["IdentitySession"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeStockSourceSetup"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveStockSourceSetup"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSetupRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectStockSourceSetup"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawStockSourceSetup"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSetupReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSetupHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/stock-counts": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecordStockCount"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/stock-counts/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockCountRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/stock-counts/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockCountReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/stock-counts/packages/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockCountPackages"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/stock-counts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockCountHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/stock-counts/warehouses/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockCountWarehouses"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["Organizations"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["Organization"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["OrganizationProposals"];
        put?: never;
        post: operations["ProposeOrganization"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations/proposals/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["OrganizationProposal"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations/proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveOrganization"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations/proposals/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectOrganization"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/organizations/proposals/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawOrganization"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/system/status": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["SystemStatus"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/auth/login": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["BeginIdentityLogin"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/auth/logout": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IdentityLogout"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/auth/step-up": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["BeginIdentityStepUp"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/health/live": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["Liveness"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/health/ready": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["Readiness"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AccessCatalogDto: {
            allowedActions: string[];
            financePacks: components["schemas"]["AccessPackDto"][];
            permissions: components["schemas"]["AccessPermissionDto"][];
            roles: components["schemas"]["AccessRoleDto"][];
        };
        AccessChangeDto: {
            grantVersion: string;
            id: components["schemas"]["LongId"];
            status: string;
            subjectId: components["schemas"]["LongId"];
            version: string;
        };
        AccessGrantDto: {
            allowedActions: string[];
            delegablePermissions: string[];
            /** Format: date-time */
            effectiveAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            id: components["schemas"]["LongId"];
            membershipId: components["schemas"]["LongId"];
            permissions: string[];
            scope: components["schemas"]["AccessScopeDto"];
            status: string;
            templateId: string;
            /** Format: int32 */
            templateVersion: number | string;
            version: string;
        };
        AccessHistoryEntryDto: {
            actorId: components["schemas"]["LongId"];
            afterVersion: null | string;
            beforeVersion: null | string;
            /** Format: date-time */
            createdAt: string;
            id: components["schemas"]["LongId"];
            operation: string;
            reason: string;
            resourceId: null | components["schemas"]["LongId"];
            scope: null | components["schemas"]["AccessScopeDto"];
            status: null | string;
            subjectId: components["schemas"]["LongId"];
        };
        AccessHistoryPageDto: {
            items: components["schemas"]["AccessHistoryEntryDto"][];
            total: string;
        };
        AccessHistoryQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number | string;
            /**
             * Format: int32
             * @default 0
             */
            offset: number | string;
            purpose: string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            subjectId?: null | string;
        };
        AccessMembershipDto: {
            allowedActions: string[];
            /** Format: date-time */
            effectiveAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            id: components["schemas"]["LongId"];
            scope: components["schemas"]["AccessScopeDto"];
            status: string;
            version: string;
        };
        AccessNodeDto: {
            id: components["schemas"]["LongId"];
            kind: components["schemas"]["ScopeNodeKind"];
            parentId: null | components["schemas"]["LongId"];
            status: string;
            version: string;
        };
        AccessPackDto: {
            id: string;
            label: string;
            permissions: string[];
        };
        AccessPermissionDto: {
            allowedScopes: components["schemas"]["GrantScopeKind"][];
            id: string;
            label: string;
            requiresFreshMfa: boolean;
        };
        AccessPreviewDto: {
            delegablePermissions: string[];
            /** Format: date-time */
            effectiveAt: string;
            existingPermissions: string[];
            /** Format: date-time */
            expiresAt: null | string;
            membershipId: components["schemas"]["LongId"];
            permissions: string[];
            requiresIndependentApproval: boolean;
            scope: components["schemas"]["AccessScopeDto"];
            templateId: string;
            /** Format: int32 */
            templateVersion: number | string;
            userId: components["schemas"]["LongId"];
        };
        AccessProposalDto: {
            allowedActions: string[];
            /** Format: date-time */
            createdAt: string;
            delegablePermissions: string[];
            /** Format: date-time */
            effectiveAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            id: components["schemas"]["LongId"];
            kind: string;
            membershipId: null | components["schemas"]["LongId"];
            permissions: string[];
            reason: string;
            requestedBy: components["schemas"]["LongId"];
            scope: components["schemas"]["AccessScopeDto"];
            status: string;
            subjectId: components["schemas"]["LongId"];
            templateId: null | string;
            /** Format: int32 */
            templateVersion: null | number | string;
            version: string;
        };
        AccessProposalResultDto: {
            id: components["schemas"]["LongId"];
            kind: string;
            status: string;
            subjectId: components["schemas"]["LongId"];
            version: string;
        };
        AccessReasonDto: {
            reason: string;
        };
        AccessRoleDto: {
            id: string;
            label: string;
            permissions: string[];
            suggestedPermissions?: null | string[];
            /** Format: int32 */
            version: number | string;
        };
        AccessScopeDto: {
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            branchId?: null | string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            channelId?: null | string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            divisionId?: null | string;
            kind: components["schemas"]["GrantScopeKind"];
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            organizationId?: null | string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            shopId?: null | string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            warehouseId?: null | string;
        };
        AccessSubjectDto: {
            grants: components["schemas"]["AccessGrantDto"][];
            grantVersion: string;
            memberships: components["schemas"]["AccessMembershipDto"][];
            status: string;
            userId: components["schemas"]["LongId"];
        };
        ApiProblem: {
            code: string;
            /** Format: int32 */
            status: number | string;
            title: string;
            traceId: string;
            type: string;
        };
        CatalogAssortmentDto: {
            id: components["schemas"]["LongId"];
            label: string;
            owner: components["schemas"]["AccessScopeDto"];
            status: string;
            version: string;
        };
        CatalogAssortmentPageDto: {
            items: components["schemas"]["CatalogAssortmentDto"][];
            total: string;
        };
        CatalogAssortmentVersionDto: {
            id: components["schemas"]["LongId"];
            version: string;
        };
        CatalogAttributeDto: {
            key: string;
            value: string;
        };
        CatalogCategoryDto: {
            active: boolean;
            code: string;
            id: components["schemas"]["LongId"];
            name: string;
            organizationId: components["schemas"]["LongId"];
            owner: components["schemas"]["AccessScopeDto"];
            version: string;
        };
        CatalogCategoryPageDto: {
            items: components["schemas"]["CatalogCategoryDto"][];
            total: string;
        };
        CatalogCategorySaveDto: {
            categoryId: null | components["schemas"]["LongId"];
            code: string;
            name: string;
            owner: components["schemas"]["AccessScopeDto"];
            reason: string;
        };
        CatalogDefinitionDto: {
            attributes: components["schemas"]["CatalogAttributeDto"][];
            baseUnitCode: string;
            /** Format: int32 */
            baseUnitsPerPackage: number | string;
            /** Format: date-time */
            effectiveAt: string;
            id: components["schemas"]["LongId"];
            /** Format: int32 */
            minimumOrderPackages: number | string;
            /** Format: int32 */
            orderIncrementPackages: number | string;
            packageKind: string;
            packageLabel: string;
            skuId: components["schemas"]["LongId"];
            version: string;
        };
        CatalogDefinitionInputDto: {
            attributes: components["schemas"]["CatalogAttributeDto"][];
            baseUnitCode: string;
            /** Format: int32 */
            baseUnitsPerPackage: number;
            imageAssetIds: components["schemas"]["LongId"][];
            /** Format: int32 */
            minimumOrderPackages: number;
            /** Format: int32 */
            orderIncrementPackages: number;
            packageKind: string;
            packageLabel: string;
        };
        CatalogDefinitionSaveDto: {
            definition: components["schemas"]["CatalogDefinitionInputDto"];
            reason: string;
        };
        CatalogFamilyCreateDto: {
            categoryId: components["schemas"]["LongId"];
            description: string;
            imageAssetIds: components["schemas"]["LongId"][];
            name: string;
            owner: components["schemas"]["AccessScopeDto"];
            reason: string;
        };
        CatalogFamilyDto: {
            active: boolean;
            categoryCode: string;
            categoryId: components["schemas"]["LongId"];
            description: string;
            id: components["schemas"]["LongId"];
            name: string;
            organizationId: components["schemas"]["LongId"];
            owner: components["schemas"]["AccessScopeDto"];
            publicId: string;
            version: string;
        };
        CatalogFamilyEditDto: {
            categoryId: components["schemas"]["LongId"];
            description: string;
            imageAssetIds: components["schemas"]["LongId"][];
            name: string;
            reason: string;
        };
        CatalogFamilyPageDto: {
            items: components["schemas"]["CatalogFamilyDto"][];
            total: string;
        };
        CatalogFamilyQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            owner: components["schemas"]["AccessScopeDto"];
            purpose: string;
            search?: null | string;
        };
        CatalogMutationResultDto: {
            currentDefinitionId: null | components["schemas"]["LongId"];
            definitionVersion: null | string;
            familyId: null | components["schemas"]["LongId"];
            id: components["schemas"]["LongId"];
            kind: string;
            predecessorSkuId: null | components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            version: string;
        };
        CatalogOwnerOptionDto: {
            label: string;
            owner: components["schemas"]["AccessScopeDto"];
        };
        CatalogOwnerOptionsDto: {
            items: components["schemas"]["CatalogOwnerOptionDto"][];
        };
        CatalogOwnerPageQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            owner: components["schemas"]["AccessScopeDto"];
            purpose: string;
        };
        CatalogPublicationDto: {
            assortmentId: components["schemas"]["LongId"];
            owner: components["schemas"]["AccessScopeDto"];
            status: string;
            version: string;
        };
        CatalogPublicationSaveDto: {
            assortmentIds: components["schemas"]["LongId"][];
            expectedAssortments: components["schemas"]["CatalogAssortmentVersionDto"][];
            reason: string;
        };
        CatalogPublishedVariantDto: {
            categoryCode: string;
            code: string;
            definition: components["schemas"]["CatalogDefinitionDto"];
            description: string;
            familyId: components["schemas"]["LongId"];
            familyName: string;
            familyPublicId: string;
            publicId: string;
            skuId: components["schemas"]["LongId"];
        };
        CatalogPublishedVariantPageDto: {
            items: components["schemas"]["CatalogPublishedVariantDto"][];
            total: string;
        };
        CatalogReadDto: {
            purpose: string;
        };
        CatalogReasonDto: {
            reason: string;
        };
        CatalogReceiptDto: {
            /** Format: date-time */
            recordedAt: null | string;
            result: null | components["schemas"]["CatalogMutationResultDto"];
            state: string;
        };
        CatalogReceiptQueryDto: {
            key: string;
            operation: string;
            purpose: string;
        };
        CatalogSkuCreateDto: {
            code: string;
            definition: components["schemas"]["CatalogDefinitionInputDto"];
            physicalItemKey: string;
            reason: string;
        };
        CatalogSkuDto: {
            active: boolean;
            code: string;
            currentDefinitionId: components["schemas"]["LongId"];
            definition: components["schemas"]["CatalogDefinitionDto"];
            familyId: components["schemas"]["LongId"];
            id: components["schemas"]["LongId"];
            organizationId: components["schemas"]["LongId"];
            owner: components["schemas"]["AccessScopeDto"];
            physicalItemKey: string;
            predecessorSkuId: null | components["schemas"]["LongId"];
            publications: components["schemas"]["CatalogPublicationDto"][];
            publicId: string;
            version: string;
        };
        CatalogSkuPageDto: {
            items: components["schemas"]["CatalogSkuDto"][];
            total: string;
        };
        CatalogSkuQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            purpose: string;
        };
        CatalogVariantQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            search?: null | string;
        };
        CatalogVariantReadDto: Record<string, never>;
        CsrfTokenResponse: {
            requestToken: string;
        };
        CurrentAccessDto: {
            allowedActions: string[];
            grants: components["schemas"]["AccessGrantDto"][];
            grantVersion: string;
            memberships: components["schemas"]["AccessMembershipDto"][];
            scopes: components["schemas"]["AccessNodeDto"][];
            status: string;
            userId: components["schemas"]["LongId"];
        };
        GrantProposalDto: {
            grant: components["schemas"]["GrantRequestDto"];
            reason: string;
        };
        GrantRequestDto: {
            additionalPermissions: string[];
            delegablePermissions?: null | string[];
            /** Format: date-time */
            effectiveAt: string;
            /** Format: date-time */
            expiresAt?: null | string;
            financePacks: string[];
            membershipId: components["schemas"]["LongId"];
            scope: components["schemas"]["AccessScopeDto"];
            templateId: string;
            userId: components["schemas"]["LongId"];
        };
        /** @enum {unknown} */
        GrantScopeKind: "buyerOwned" | "buyingDivision" | "buyingBranch" | "buyingOrganization" | "fulfillmentShop" | "fulfillmentWarehouse" | "courierAssignment" | "system";
        HealthStatus: {
            status: string;
        };
        HistoricalGrantProposalDto: {
            grant: components["schemas"]["HistoricalGrantRequestDto"];
            reason: string;
        };
        HistoricalGrantRequestDto: {
            /** Format: date-time */
            effectiveAt: string;
            /** Format: date-time */
            expiresAt?: null | string;
            membershipId: components["schemas"]["LongId"];
            permissions: string[];
            scope: components["schemas"]["AccessScopeDto"];
            userId: components["schemas"]["LongId"];
        };
        HistoricalTargetDto: {
            code: null | string;
            id: components["schemas"]["LongId"];
            kind: components["schemas"]["ScopeNodeKind"];
            name: null | string;
            parentId: null | components["schemas"]["LongId"];
            scope: components["schemas"]["AccessScopeDto"];
            status: string;
            version: string;
        };
        HistoricalTargetPageDto: {
            items: components["schemas"]["HistoricalTargetDto"][];
            total: string;
        };
        HistoricalTargetQueryDto: {
            /**
             * Format: int32
             * @default 100
             */
            limit: number | string;
            membershipId: components["schemas"]["LongId"];
            /**
             * Format: int32
             * @default 0
             */
            offset: number | string;
            userId: components["schemas"]["LongId"];
        };
        IdentityAccountDto: {
            displayName: null | string;
            userId: components["schemas"]["LongId"];
            verifiedEmail: string;
        };
        IdentityDecisionDto: {
            reason: string;
        };
        IdentityDirectoryPageDto: {
            items: components["schemas"]["IdentityProfileDto"][];
            total: string;
        };
        IdentityDirectoryQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number | string;
            /**
             * Format: int32
             * @default 0
             */
            offset: number | string;
            purpose: string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            userId?: null | string;
        };
        IdentityDirectorySearchDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number | string;
            /**
             * Format: int32
             * @default 0
             */
            offset: number | string;
            purpose: string;
            term: string;
        };
        IdentityInvitationDto: {
            /** Format: date-time */
            accessExpiresAt: null | string;
            allowedActions: string[];
            approvedBy: null | components["schemas"]["LongId"];
            /** Format: date-time */
            expiresAt: string;
            id: components["schemas"]["LongId"];
            intendedEmail: string;
            reason: string;
            requestedBy: components["schemas"]["LongId"];
            role: null | components["schemas"]["IdentityInvitationRoleDto"];
            scope: components["schemas"]["AccessScopeDto"];
            status: string;
            userId: components["schemas"]["LongId"];
            version: string;
        };
        IdentityInvitationPageDto: {
            items: components["schemas"]["IdentityInvitationDto"][];
            total: string;
        };
        IdentityInvitationProposalDto: {
            /** Format: date-time */
            accessExpiresAt?: null | string;
            email: string;
            /** Format: date-time */
            expiresAt: string;
            reason: string;
            role?: null | components["schemas"]["IdentityInvitationRoleRequestDto"];
            scope: components["schemas"]["AccessScopeDto"];
        };
        IdentityInvitationQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number | string;
            /**
             * Format: int32
             * @default 0
             */
            offset: number | string;
            purpose: string;
        };
        IdentityInvitationResultDto: {
            id: components["schemas"]["LongId"];
            status: string;
            userId: components["schemas"]["LongId"];
            version: string;
        };
        IdentityInvitationRoleDto: {
            delegablePermissions: string[];
            permissions: string[];
            templateId: string;
            /** Format: int32 */
            templateVersion: number | string;
        };
        IdentityInvitationRoleRequestDto: {
            additionalPermissions: string[];
            delegablePermissions: string[];
            financePacks: string[];
            templateId: string;
        };
        IdentityLoginForm: {
            __RequestVerificationToken: string;
            invitationId: null | string;
            invitationSecret: null | string;
            returnPath: null | string;
        };
        IdentityLogoutForm: {
            __RequestVerificationToken: string;
            expectedActorId: string;
        };
        IdentityProfileDto: {
            displayName: null | string;
            grantVersion: string;
            securityVersion: string;
            status: string;
            userId: components["schemas"]["LongId"];
            /** Format: date-time */
            verifiedAt: string;
            verifiedEmail: string;
        };
        IdentityRecoveryRequestDto: {
            kind: string;
            reason: string;
            userId: components["schemas"]["LongId"];
        };
        IdentityRecoveryResultDto: {
            id: components["schemas"]["LongId"];
            securityVersion: string;
            status: string;
            userId: components["schemas"]["LongId"];
        };
        IdentitySessionDto: {
            /** Format: date-time */
            absoluteExpiresAt: null | string;
            account: null | components["schemas"]["IdentityAccountDto"];
            authenticated: boolean;
            /** Format: date-time */
            factorCompletedAt: null | string;
            /** Format: date-time */
            freshFactorUntil: null | string;
            /** Format: date-time */
            idleExpiresAt: null | string;
            /** Format: date-time */
            serverTime: string;
            stepUpOperations: string[];
        };
        IdentityStepUpForm: {
            __RequestVerificationToken: string;
            expectedActorId: string;
            operation: string;
            returnPath: null | string;
        };
        /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
        LongId: string;
        MembershipProposalDto: {
            membership: components["schemas"]["MembershipRequestDto"];
            reason: string;
        };
        MembershipRequestDto: {
            /** Format: date-time */
            effectiveAt: string;
            /** Format: date-time */
            expiresAt?: null | string;
            scope: components["schemas"]["AccessScopeDto"];
            userId: components["schemas"]["LongId"];
        };
        OperationLookupDto: {
            key: string;
            operation: string;
            purpose: string;
        };
        OperationReceiptDto: {
            operation: string;
            /** Format: date-time */
            recordedAt: null | string;
            result: null | components["schemas"]["OperationReceiptResultDto"];
            state: string;
        };
        OperationReceiptResultDto: {
            grantVersion: null | string;
            id: components["schemas"]["LongId"];
            kind: null | string;
            securityVersion: null | string;
            status: string;
            subjectId: components["schemas"]["LongId"];
            version: string;
        };
        OperationResolveDto: {
            operation: string;
            reason: string;
        };
        OrganizationBeforeDto: {
            code: null | string;
            name: null | string;
            profileVersion: string;
            scopeVersion: string;
            status: null | string;
        };
        /** @enum {unknown} */
        OrganizationChangeKind: "create" | "updateProfile" | "reactivate";
        OrganizationChangeResultDto: {
            affectedAccounts: string;
            profileVersion: null | string;
            proposalId: components["schemas"]["LongId"];
            proposalVersion: string;
            scopeId: null | components["schemas"]["LongId"];
            scopeVersion: null | string;
            status: string;
        };
        OrganizationDto: {
            allowedActions: string[];
            code: null | string;
            id: components["schemas"]["LongId"];
            kind: components["schemas"]["ScopeNodeKind"];
            name: null | string;
            parentId: null | components["schemas"]["LongId"];
            profileVersion: null | string;
            scopeVersion: string;
            status: string;
        };
        OrganizationImpactDto: {
            activeGrants: string;
            activeMemberships: string;
            affectedAccounts: string;
            affectedScopes: string;
            revision: string;
        };
        OrganizationPageDto: {
            allowedActions: string[];
            items: components["schemas"]["OrganizationDto"][];
            total: string;
        };
        OrganizationProposalDto: {
            allowedActions: string[];
            /** Format: date-time */
            approvedAt: null | string;
            approvedBy: null | components["schemas"]["LongId"];
            before: components["schemas"]["OrganizationBeforeDto"];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            decisionAt: null | string;
            decisionBy: null | components["schemas"]["LongId"];
            decisionReason: null | string;
            id: components["schemas"]["LongId"];
            impact: components["schemas"]["OrganizationImpactDto"];
            reason: string;
            request: components["schemas"]["OrganizationRequestDto"];
            requestedBy: components["schemas"]["LongId"];
            resultScopeId: null | components["schemas"]["LongId"];
            status: string;
            version: string;
        };
        OrganizationProposalPageDto: {
            allowedActions: string[];
            items: components["schemas"]["OrganizationProposalDto"][];
            total: string;
        };
        OrganizationProposalRequestDto: {
            change: components["schemas"]["OrganizationRequestDto"];
            reason: string;
        };
        OrganizationProposalResultDto: {
            id: components["schemas"]["LongId"];
            status: string;
            version: string;
        };
        OrganizationRequestDto: {
            changeKind: components["schemas"]["OrganizationChangeKind"];
            code: string;
            /** @default 0 */
            expectedProfileVersion: string;
            /** @default 0 */
            expectedScopeVersion: string;
            kind: components["schemas"]["ScopeNodeKind"];
            name: string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            parentId?: null | string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            scopeId?: null | string;
        };
        PersonReviewAccountDto: {
            displayName: null | string;
            status: string;
            userId: components["schemas"]["LongId"];
            verifiedEmail: string;
        };
        PersonReviewDecisionDto: {
            actor: components["schemas"]["PersonReviewAccountDto"];
            /** Format: date-time */
            decidedAt: string;
            reason: string;
        };
        PersonReviewDetailDto: {
            accounts: components["schemas"]["PersonReviewAccountDto"][];
            decision: null | components["schemas"]["PersonReviewDecisionDto"];
            summary: components["schemas"]["PersonReviewSummaryDto"];
        };
        PersonReviewListDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            purpose: string;
            status?: null | string;
        };
        PersonReviewPageDto: {
            items: components["schemas"]["PersonReviewSummaryDto"][];
            total: string;
        };
        PersonReviewPreviewDto: {
            /** Format: date-time */
            expiresAt: string;
            kind: string;
            proposal: null | components["schemas"]["PersonReviewSummaryDto"];
            reference: string;
            target: components["schemas"]["PersonReviewTargetDto"];
        };
        PersonReviewReadDto: {
            purpose: string;
        };
        PersonReviewReceiptDto: {
            /** Format: date-time */
            recordedAt: null | string;
            result: null | components["schemas"]["PersonReviewResultDto"];
            state: string;
        };
        PersonReviewReceiptQueryDto: {
            key: string;
            operation: string;
            purpose: string;
        };
        PersonReviewResolutionDto: {
            operation: string;
            reason: string;
        };
        PersonReviewResultDto: {
            groupId: components["schemas"]["LongId"];
            groupVersion: string;
            proposalId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            status: string;
            version: string;
        };
        PersonReviewSearchDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            purpose: string;
            term: string;
        };
        PersonReviewSubmissionDto: {
            previewReference: string;
            reason: string;
        };
        PersonReviewSummaryDto: {
            affectedAccountCount: string;
            allowedActions: string[];
            reason: string;
            requester: components["schemas"]["PersonReviewAccountDto"];
            result: components["schemas"]["PersonReviewResultDto"];
        };
        PersonReviewTargetDto: {
            accounts: components["schemas"]["PersonReviewAccountDto"][];
            groupId: components["schemas"]["LongId"];
            status: string;
            version: string;
        };
        PersonReviewTargetPageDto: {
            items: components["schemas"]["PersonReviewTargetDto"][];
            total: string;
        };
        /** @enum {unknown} */
        ScopeNodeKind: "buyingOrganization" | "fulfillmentOrganization" | "branch" | "division" | "shop" | "warehouse" | "channel";
        StockCountObservationDto: {
            baseline: string;
            baseUnitCode: string;
            baseUnits: string;
            /** Format: int32 */
            baseUnitsPerPackage: number | string;
            coverage: string;
            definitionId: components["schemas"]["LongId"];
            definitionVersion: string;
            id: components["schemas"]["LongId"];
            location: components["schemas"]["AccessScopeDto"];
            observedLotLabel: null | string;
            packageKind: string;
            packageLabel: string;
            /** Format: int32 */
            packages: number | string;
            reason: string;
            /** Format: date-time */
            recordedAt: string;
            skuCode: string;
            skuId: components["schemas"]["LongId"];
            version: string;
        };
        StockCountObservationPageDto: {
            items: components["schemas"]["StockCountObservationDto"][];
            total: string;
        };
        StockCountPackageOptionDto: {
            baseUnitCode: string;
            /** Format: int32 */
            baseUnitsPerPackage: number | string;
            definitionId: components["schemas"]["LongId"];
            definitionVersion: string;
            familyName: string;
            packageKind: string;
            packageLabel: string;
            skuCode: string;
            skuId: components["schemas"]["LongId"];
            skuRevision: string;
        };
        StockCountPackagePageDto: {
            items: components["schemas"]["StockCountPackageOptionDto"][];
            total: string;
        };
        StockCountPackageQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            location: components["schemas"]["AccessScopeDto"];
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            purpose: string;
            search?: null | string;
        };
        StockCountPageQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            location: components["schemas"]["AccessScopeDto"];
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            purpose: string;
        };
        StockCountReadDto: {
            purpose: string;
        };
        StockCountReceiptDto: {
            /** Format: date-time */
            recordedAt: null | string;
            result: null | components["schemas"]["StockCountReceiptResultDto"];
            state: string;
        };
        StockCountReceiptQueryDto: {
            key: string;
            operation: string;
            purpose: string;
        };
        StockCountReceiptResultDto: {
            id: components["schemas"]["LongId"];
            version: string;
        };
        StockCountRecordDto: {
            expectedDefinitionId: components["schemas"]["LongId"];
            expectedSkuRevision: components["schemas"]["LongId"];
            location: components["schemas"]["AccessScopeDto"];
            observedLotLabel?: null | string;
            /** Format: int32 */
            packages: number;
            reason: string;
            skuId: components["schemas"]["LongId"];
        };
        StockCountWarehouseOptionDto: {
            channelName: string;
            location: components["schemas"]["AccessScopeDto"];
            organizationName: string;
            shopName: string;
            warehouseName: string;
        };
        StockCountWarehouseOptionsDto: {
            items: components["schemas"]["StockCountWarehouseOptionDto"][];
        };
        StockSourceSetupCommandDto: {
            decisionId: null | components["schemas"]["LongId"];
            physicalPoolId: null | components["schemas"]["LongId"];
            proposalId: components["schemas"]["LongId"];
            proposalVersion: string;
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourceSetupDecideDto: {
            reason: string;
            reviewReference: string;
        };
        StockSourceSetupDecisionDto: {
            /** Format: date-time */
            decidedAt: string;
            reason: string;
            reviewReference: string;
            /** @enum {string} */
            status: "approved" | "rejected" | "withdrawn";
        };
        StockSourceSetupDetailDto: {
            /** @enum {string} */
            control: "exclusiveWebAllocation" | "sharedWebLedger" | "externalHold";
            decision: null | components["schemas"]["StockSourceSetupDecisionDto"];
            discrepancyOwnerReference: string;
            externalNamespace: null | string;
            handoffReference: string;
            id: components["schemas"]["LongId"];
            label: string;
            location: components["schemas"]["AccessScopeDto"];
            /** @description Exact positive Int64 milliseconds; the service additionally checks the supported TimeSpan range. */
            maximumAgeMilliseconds: string;
            /** @enum {string} */
            mode: "webLedger" | "externalLedger";
            physicalKey: string;
            physicalPoolId: null | components["schemas"]["LongId"];
            proposalVersion: string;
            /** Format: date-time */
            proposedAt: string;
            reason: string;
            reservationProtocolReference: null | string;
            /** @description Exact nonnegative Int64 base-unit quantity. */
            safetyBufferBaseUnits: string;
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourceSetupPageDto: {
            items: components["schemas"]["StockSourceSetupDetailDto"][];
            total: string;
        };
        StockSourceSetupProposeDto: {
            /** @enum {string} */
            control: "exclusiveWebAllocation" | "sharedWebLedger" | "externalHold";
            discrepancyOwnerReference: string;
            externalNamespace?: null | string;
            handoffReference: string;
            label: string;
            location: components["schemas"]["AccessScopeDto"];
            /** @description Exact positive Int64 milliseconds; the service additionally checks the supported TimeSpan range. */
            maximumAgeMilliseconds: string;
            /** @enum {string} */
            mode: "webLedger" | "externalLedger";
            physicalKey: string;
            reason: string;
            reservationProtocolReference?: null | string;
            /** @description Exact nonnegative Int64 base-unit quantity. */
            safetyBufferBaseUnits: string;
        };
        StockSourceSetupQueryDto: {
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            location: components["schemas"]["AccessScopeDto"];
            /**
             * Format: int32
             * @default 0
             */
            offset: number;
            purpose: string;
        };
        StockSourceSetupReceiptDto: {
            result: null | components["schemas"]["StockSourceSetupCommandDto"];
            /** @enum {string} */
            state: "not_confirmed" | "committed";
        };
        StockSourceSetupRecoveryDto: {
            key: string;
            location: components["schemas"]["AccessScopeDto"];
            /** @enum {string} */
            operation: "stock.source.propose" | "stock.source.approve" | "stock.source.reject" | "stock.source.withdraw";
            purpose: string;
        };
        SystemStatus: {
            status: string;
            /** Format: date-time */
            utcNow: string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    AccessCatalog: {
        parameters: {
            query?: never;
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessCatalogDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CurrentAccess: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CurrentAccessDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    RevokeAccessGrant: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessChangeDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PreviewAccessGrant: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["GrantRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessPreviewDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ProposeAccessGrant: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["GrantProposalDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessProposalResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ApproveAccessGrant: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessChangeDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PreviewHistoricalAccessGrant: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["HistoricalGrantRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessPreviewDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ProposeHistoricalAccessGrant: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["HistoricalGrantProposalDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessProposalResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    QueryHistoricalAccessTargets: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["HistoricalTargetQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HistoricalTargetPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    AccessHistory: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessHistoryQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessHistoryPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    SuspendMembership: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessChangeDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ProposeMembership: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["MembershipProposalDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessProposalResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ApproveMembership: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessChangeDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    LookupAccessOperation: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OperationLookupDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OperationReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ResolveAccessOperation: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OperationResolveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OperationReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    AccessProposals: {
        parameters: {
            query?: {
                limit?: number | string;
                offset?: number | string;
            };
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessProposalDto"][];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    RejectAccessProposal: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessProposalResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    WithdrawAccessProposal: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessProposalResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    SuspendAccessScope: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessChangeDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    SuspendIdentity: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessChangeDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    AccessSubject: {
        parameters: {
            query?: never;
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path: {
                userId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AccessSubjectDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogAssortmentQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogOwnerPageQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogAssortmentPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogCategorySave: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogCategorySaveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogCategoryQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogOwnerPageQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogCategoryPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogFamilyCreate: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogFamilyCreateDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogFamilyEdit: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogFamilyEditDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogFamilyRead: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogFamilyDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogSkuCreate: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogSkuCreateDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogSkuQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogSkuQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogSkuPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogFamilyQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogFamilyQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogFamilyPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogReceiptQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogReceiptQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogOwnerOptions: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogOwnerOptionsDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogDefinitionRevise: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogDefinitionSaveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogSkuPublish: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogPublicationSaveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogSkuRead: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogSkuDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogSkuReplace: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogSkuCreateDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogSkuRetire: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogMutationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogPublishedVariantRead: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                publicId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogVariantReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPublishedVariantDto"] | null;
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    CatalogPublishedVariantQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogVariantQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPublishedVariantPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationDirectoryQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDirectoryQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityDirectoryPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationDirectorySearch: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDirectorySearchDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityDirectoryPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationProposeInvitation: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityInvitationProposalDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityInvitationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationApproveInvitation: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityInvitationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationRejectInvitation: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityInvitationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationRevokeInvitation: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityInvitationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationWithdrawInvitation: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityInvitationResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationInvitationsQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityInvitationQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityInvitationPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewReceiptQuery: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewReceiptQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewReceiptResolve: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewResolutionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewPropose: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewSubmissionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewApprove: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewSubmissionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewApprovalPreview: {
        parameters: {
            query?: never;
            header: {
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewPreviewDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewDetail: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewDetailDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewReject: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewWithdraw: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewList: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewListDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewProposalPreview: {
        parameters: {
            query?: never;
            header: {
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewPreviewDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    PersonReviewTargetSearch: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PersonReviewSearchDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PersonReviewTargetPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityAdministrationRequestRecovery: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["IdentityRecoveryRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentityRecoveryResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityCsrf: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CsrfTokenResponse"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    Auth0SecurityEvents: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unsupported Media Type */
            415: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    Auth0SecurityLogs: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unsupported Media Type */
            415: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentitySession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["IdentitySessionDto"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ProposeStockSourceSetup: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupProposeDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupCommandDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ApproveStockSourceSetup: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupDecideDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupCommandDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockSourceSetupRead: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupDetailDto"] | null;
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    RejectStockSourceSetup: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupDecideDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupCommandDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    WithdrawStockSourceSetup: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupDecideDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupCommandDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockSourceSetupReceipt: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockSourceSetupHistory: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSetupQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSetupPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    RecordStockCount: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockCountRecordDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockCountObservationDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockCountRead: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockCountReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockCountObservationDto"] | null;
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockCountReceipt: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockCountReceiptQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockCountReceiptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockCountPackages: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockCountPackageQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockCountPackagePageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockCountHistory: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockCountPageQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockCountObservationPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    StockCountWarehouses: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockCountReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockCountWarehouseOptionsDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    Organizations: {
        parameters: {
            query?: {
                limit?: number | string;
                offset?: number | string;
            };
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    Organization: {
        parameters: {
            query?: never;
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    OrganizationProposals: {
        parameters: {
            query?: {
                limit?: number | string;
                offset?: number | string;
            };
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationProposalPageDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ProposeOrganization: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrganizationProposalRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationProposalResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    OrganizationProposal: {
        parameters: {
            query?: never;
            header?: {
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id"?: string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationProposalDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    ApproveOrganization: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationChangeResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    RejectOrganization: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationChangeResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    WithdrawOrganization: {
        parameters: {
            query?: never;
            header: {
                /** @description Reuse this key only for an unchanged logical command, including after an uncertain response. */
                "Idempotency-Key": string;
                /** @description One quoted nonnegative Int64 resource version from the latest response; never a JavaScript number. */
                "If-Match": string;
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AccessReasonDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrganizationChangeResultDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    SystemStatus: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SystemStatus"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    BeginIdentityLogin: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/x-www-form-urlencoded": components["schemas"]["IdentityLoginForm"];
            };
        };
        responses: {
            /** @description Found */
            302: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    IdentityLogout: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/x-www-form-urlencoded": components["schemas"]["IdentityLogoutForm"];
            };
        };
        responses: {
            /** @description Found */
            302: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    BeginIdentityStepUp: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/x-www-form-urlencoded": components["schemas"]["IdentityStepUpForm"];
            };
        };
        responses: {
            /** @description Found */
            302: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
    Liveness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HealthStatus"];
                };
            };
        };
    };
    Readiness: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HealthStatus"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ApiProblem"];
                };
            };
        };
    };
}
