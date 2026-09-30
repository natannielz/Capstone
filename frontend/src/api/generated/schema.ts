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
    "/api/v1/billing/bank-receipts/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadBankReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-receipts/confirmations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ConfirmBankReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-receipts/imports/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadBankReceiptImportConfirmations"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-receipts/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverBankReceiptConfirmation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-receipts/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewBankReceiptConfirmation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-receipts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListBankReceipts"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/accounts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListBankStatementAccounts"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/imports": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StageBankStatement"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/imports/{id}/original/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadBankStatementOriginal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/imports/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadBankStatementImport"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/imports/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListBankStatementImports"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverBankStatementImport"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewBankStatement"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/bank-statements/profiles/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListBankStatementProfiles"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IssueInvoice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices/{id}/download": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["DownloadInvoice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadInvoice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices/candidates/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListInvoiceCandidates"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverInvoice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices/preview/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewInvoice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/invoices/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListInvoices"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SavePaymentDeclaration"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReservePaymentEvidence"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/content": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["UploadPaymentEvidence"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/original/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentEvidenceOriginal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentEvidenceStatus"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence/{reservationId}/recover": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverPaymentEvidenceUpload"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverPaymentEvidenceReservation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/evidence/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListPaymentEvidence"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/ocr/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentOcr"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentDeclaration"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/submission/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverPaymentSubmission"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/{id}/submit": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SubmitPaymentDeclaration"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverPaymentDeclaration"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-declarations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListPaymentDeclarations"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/{id}/corrections": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CorrectPaymentReview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/{id}/corrections/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverPaymentCorrection"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/{id}/corrections/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentCorrections"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/{id}/evidence/{reservationId}/original/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentReviewOriginal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/{id}/ocr/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentReviewOcr"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadPaymentReview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/payment-reviews/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListPaymentReviews"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverReceivingAccountOperation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/policies/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListReceivingAccountPolicies"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeReceivingAccount"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/proposals/{id}/decisions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["DecideReceivingAccount"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/proposals/{id}/observations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ObserveReceivingAccount"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/proposals/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadReceivingAccountProposal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/billing/receiving-accounts/proposals/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListReceivingAccountProposals"];
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
    "/api/v1/catalog/prices/proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveCatalogPrice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/proposals/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectCatalogPrice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/proposals/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawCatalogPrice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/current/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogPriceCurrent"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/history/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogPriceHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverCatalogPriceOperation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/preview/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogPricePreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeCatalogPrice"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/proposals/{proposalId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CatalogPriceProposal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/{id}/proposals/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListCatalogPriceProposals"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/catalog/prices/skus/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SelectCatalogPriceSkus"];
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
    "/api/v1/fulfillment/delivery-attempts/{shipmentId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecordDeliveryAttempt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/delivery-attempts/{shipmentId}/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewDeliveryAttempt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/delivery-attempts/{shipmentId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadDeliveryAttempts"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/delivery-attempts/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverDeliveryAttempt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/dispatches/{suborderId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["DispatchShipment"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/dispatches/{suborderId}/couriers/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListShipmentDispatchCouriers"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/dispatches/{suborderId}/preview/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewShipmentDispatch"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/dispatches/{suborderId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipmentDispatchPreparation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/dispatches/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverShipmentDispatch"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/dispatches/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListShipmentDispatchQueue"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/preparations/{suborderId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SaveShipmentPreparation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/preparations/{suborderId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipmentPreparation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/preparations/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverShipmentPreparation"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/preparations/sources/{suborderId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipmentPreparationSource"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/preparations/sources/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListShipmentPreparationSources"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipt-proofs/{shipmentId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IssueShipmentReceiptProof"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipt-proofs/{shipmentId}/{documentId}/download": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["DownloadShipmentReceiptProof"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipt-proofs/{shipmentId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipmentReceiptProof"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipt-proofs/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverShipmentReceiptProof"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/{shipmentId}/challenges": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IssueShipmentReceiptChallenge"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/{shipmentId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipmentReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/{shipmentId}/recipients/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListShipmentReceiptRecipients"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/challenge-operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverShipmentReceiptChallenge"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/challenges/{challengeId}/confirm": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ConfirmShipmentReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/challenges/{challengeId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReviewShipmentReceiptChallenge"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/receipts/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverShipmentReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/shipment-documents/{shipmentId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["IssueShipmentDocument"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/shipment-documents/{shipmentId}/{documentId}/download": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["DownloadShipmentDocument"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/shipment-documents/{shipmentId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipmentDocument"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/shipment-documents/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverShipmentDocument"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/shipments/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadShipment"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/fulfillment/shipments/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListShipments"];
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
    "/api/v1/inventory/sources/alias-operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceAliasReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/alias-proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveStockSourceAlias"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/alias-proposals/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectStockSourceAlias"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/alias-proposals/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawStockSourceAlias"];
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
    "/api/v1/inventory/sources/policy-operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourcePolicyReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/policy-proposals/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveStockSourcePolicy"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/policy-proposals/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectStockSourcePolicy"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/policy-proposals/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawStockSourcePolicy"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/alias-proposals/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceAliasProposal"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/alias-proposals/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceAliasHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/aliases": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeStockSourceAlias"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/aliases/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceAliasCurrent"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/policy-preview/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourcePolicyPreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/policy-proposals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeStockSourcePolicy"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/policy-proposals/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourcePolicyDetail"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/policy-proposals/{id}/review/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourcePolicyReview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/policy-proposals/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourcePolicyHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/policy/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourcePolicyLatest"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceCurrentRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/revisions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ProposeStockSourceRevision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/suspend": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SuspendStockSource"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/suspension-preview/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSuspensionPreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/suspension/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSuspensionLatest"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/suspensions/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSuspensionDetail"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/{poolId}/suspensions/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSuspensionHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/pools/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceCurrentList"];
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
    "/api/v1/inventory/sources/revision-operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceRevisionReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/revisions/{id}/approve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ApproveStockSourceRevision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/revisions/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceRevisionRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/revisions/{id}/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RejectStockSourceRevision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/revisions/{id}/withdraw": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["WithdrawStockSourceRevision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/revisions/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceRevisionHistory"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/suspension-operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceSuspensionReceipt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/inventory/sources/warehouses/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StockSourceWarehouseChoices"];
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
    "/api/v1/ordering/contexts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["OrderBuyingContexts"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/drafts": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SaveOrderDraft"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/drafts/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["OrderDraftRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/drafts/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverOrderDraftSave"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/drafts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["OrderDraftList"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/orders": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["SubmitBranchOrder"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/orders/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReadOwnBranchOrder"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/orders/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverBranchOrder"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/orders/preview/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["OrderCheckoutPreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/orders/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ListOwnBranchOrders"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/quote/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** @description Current branch-scoped catalog subtotal only. No stock hold, order submission, tax/credit approval or payable commitment. Submission must recalculate in its own transaction. */
        post: operations["OrderQuotePreview"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/reviews/{id}/decisions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecordOrderDecision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/reviews/{id}/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["OrderReviewRead"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/reviews/operations/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RecoverOrderDecision"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/v1/ordering/reviews/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["OrderReviewList"];
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
        BankReceiptConfirmDto: {
            attested: boolean;
            binding: string;
            importId: components["schemas"]["LongId"];
            observed: components["schemas"]["BankReceiptObservationDto"];
            reason: string;
            /** Format: int32 */
            rowNumber: number | string;
            sourceReference: string;
        };
        BankReceiptImportDto: {
            items: components["schemas"]["BankReceiptResultDto"][];
        };
        BankReceiptListDto: {
            hasMore: boolean;
            items: components["schemas"]["BankReceiptViewDto"][];
        };
        BankReceiptObservationDto: {
            accountNumber: string;
            amount: components["schemas"]["Money"];
            bookedDate: string;
            transactionId: string;
        };
        BankReceiptPolicyDto: {
            approvalReference: string;
            cashMapping: string;
            /** Format: date-time */
            expiresAt: string;
            id: components["schemas"]["LongId"];
            operatingOwnerReference: string;
            revision: string;
            suspenseMapping: string;
            trustedSourceReference: string;
        };
        BankReceiptPreviewDto: {
            account: components["schemas"]["StatementAccountDto"];
            binding: string;
            existingReceiptId: null | components["schemas"]["LongId"];
            importId: components["schemas"]["LongId"];
            policy: components["schemas"]["BankReceiptPolicyDto"];
            row: components["schemas"]["StatementRowDto"];
            /** Format: int32 */
            rowNumber: number | string;
        };
        BankReceiptPreviewInputDto: {
            importId: components["schemas"]["LongId"];
            /** Format: int32 */
            rowNumber: number | string;
        };
        BankReceiptRecoveryDto: {
            result: null | components["schemas"]["BankReceiptResultDto"];
            status: string;
        };
        BankReceiptResultDto: {
            confirmationId: components["schemas"]["LongId"];
            createdReceipt: boolean;
            importId: components["schemas"]["LongId"];
            operation: string;
            receiptId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            /** Format: int32 */
            rowNumber: number | string;
        };
        BankReceiptViewDto: {
            actorId: components["schemas"]["LongId"];
            amount: components["schemas"]["Money"];
            bookedDate: string;
            importId: components["schemas"]["LongId"];
            journalId: components["schemas"]["LongId"];
            policy: components["schemas"]["BankReceiptPolicyDto"];
            receiptId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            /** Format: int32 */
            rowNumber: number | string;
            transactionId: string;
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
        CatalogPriceChoiceDto: {
            definition: components["schemas"]["CatalogDefinitionDto"];
            familyName: string;
            sku: components["schemas"]["CatalogPriceSkuDto"];
        };
        CatalogPriceChoicesDto: {
            items: components["schemas"]["CatalogPriceChoiceDto"][];
        };
        CatalogPriceCommandDto: {
            decisionId: null | components["schemas"]["LongId"];
            priceId: null | components["schemas"]["LongId"];
            priceVersion: null | string;
            proposalId: components["schemas"]["LongId"];
            revision: string;
            skuId: components["schemas"]["LongId"];
            status: string;
        };
        CatalogPriceCurrentDto: {
            price: null | components["schemas"]["CatalogPriceDto"];
        };
        CatalogPriceDecideDto: {
            reason: string;
            reviewReference: string;
        };
        CatalogPriceDecisionDto: {
            action: string;
            /** Format: date-time */
            decidedAt: string;
            id: components["schemas"]["LongId"];
            reason: string;
            reviewReference: string;
        };
        CatalogPriceDto: {
            currency: string;
            /** Format: date-time */
            effectiveAt: string;
            id: components["schemas"]["LongId"];
            skuId: components["schemas"]["LongId"];
            unitPrice: components["schemas"]["Money"];
            version: string;
        };
        CatalogPriceExpectationDto: {
            definitionId: components["schemas"]["LongId"];
            definitionVersion: string;
            priceVersion: string;
            skuVersion: string;
        };
        CatalogPriceHistoryDto: {
            access: string;
            beforeVersion?: null | string;
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            purpose: string;
        };
        CatalogPriceHistoryResultDto: {
            items: components["schemas"]["CatalogPriceDto"][];
        };
        CatalogPriceInputDto: {
            currency: string;
            unitPrice: components["schemas"]["Money"];
        };
        CatalogPricePreviewDto: {
            currentPrice: null | components["schemas"]["CatalogPriceDto"];
            definition: components["schemas"]["CatalogDefinitionDto"];
            expected: components["schemas"]["CatalogPriceExpectationDto"];
            sku: components["schemas"]["CatalogPriceSkuDto"];
        };
        CatalogPriceProposalDto: {
            basePrice: null | components["schemas"]["CatalogPriceDto"];
            decision: null | components["schemas"]["CatalogPriceDecisionDto"];
            definition: components["schemas"]["CatalogDefinitionDto"];
            expected: components["schemas"]["CatalogPriceExpectationDto"];
            input: components["schemas"]["CatalogPriceInputDto"];
            price: null | components["schemas"]["CatalogPriceDto"];
            proposalId: components["schemas"]["LongId"];
            /** Format: date-time */
            proposedAt: string;
            reason: string;
            revision: string;
            sku: components["schemas"]["CatalogPriceSkuDto"];
            skuId: components["schemas"]["LongId"];
            status: string;
        };
        CatalogPriceProposalListDto: {
            access: string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            beforeId?: null | string;
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            purpose: string;
        };
        CatalogPriceProposalsDto: {
            items: components["schemas"]["CatalogPriceProposalDto"][];
        };
        CatalogPriceProposeDto: {
            expectedDefinitionId: components["schemas"]["LongId"];
            expectedDefinitionVersion: string;
            expectedPriceVersion: string;
            input: components["schemas"]["CatalogPriceInputDto"];
            reason: string;
        };
        CatalogPriceReadDto: {
            access: string;
            purpose: string;
        };
        CatalogPriceReceiptDto: {
            result: null | components["schemas"]["CatalogPriceCommandDto"];
            state: string;
        };
        CatalogPriceRecoveryDto: {
            action: string;
            key: string;
            purpose: string;
        };
        CatalogPriceSelectionDto: {
            access: string;
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            afterId?: null | string;
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            purpose: string;
            search?: null | string;
        };
        CatalogPriceSkuDto: {
            active: boolean;
            code: string;
            familyId: components["schemas"]["LongId"];
            id: components["schemas"]["LongId"];
            publicId: string;
            version: string;
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
            currentPrice: null | components["schemas"]["CatalogPriceDto"];
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
        DeliveryAttemptCommandDto: {
            /** Format: date-time */
            occurredAt: string;
            outcome: string;
            reason: string;
        };
        DeliveryAttemptDto: {
            id: components["schemas"]["LongId"];
            /** Format: date-time */
            occurredAt: string;
            outcome: string;
            reason: string;
            /** Format: date-time */
            recordedAt: string;
            /** Format: int32 */
            revokedQrCount: number | string;
            sequence: string;
            shipmentId: components["schemas"]["LongId"];
        };
        DeliveryAttemptPageDto: {
            hasMore: boolean;
            items: components["schemas"]["DeliveryAttemptDto"][];
            received: boolean;
            sequence: string;
            shipmentId: components["schemas"]["LongId"];
        };
        DeliveryAttemptPreviewDto: {
            /** Format: date-time */
            dispatchedAt: string;
            /** Format: date-time */
            lastOccurredAt: null | string;
            received: boolean;
            sequence: string;
            /** Format: date-time */
            serverTime: string;
            shipmentId: components["schemas"]["LongId"];
        };
        DeliveryAttemptQueryDto: {
            beforeSequence: null | string;
            purpose: string;
        };
        DeliveryAttemptRecoveryDto: {
            attempt: null | components["schemas"]["DeliveryAttemptDto"];
            status: string;
        };
        DispatchCommandDto: {
            reason: string;
            review: string;
            selection: components["schemas"]["DispatchSelectionDto"];
        };
        DispatchCourierDto: {
            displayName: string;
            userId: components["schemas"]["LongId"];
        };
        DispatchCouriersDto: {
            hasMore: boolean;
            items: components["schemas"]["DispatchCourierDto"][];
        };
        DispatchPreparationDto: {
            lines: components["schemas"]["ShipmentPackageDto"][];
            lots: components["schemas"]["DispatchReservedLotDto"][];
            preparationId: components["schemas"]["LongId"];
            preparationRevision: string;
            source: components["schemas"]["FulfillmentOrderDto"];
        };
        DispatchPreviewDto: {
            purpose: string;
            selection: components["schemas"]["DispatchSelectionDto"];
        };
        DispatchQueueDto: {
            hasMore: boolean;
            items: components["schemas"]["DispatchQueueItemDto"][];
        };
        DispatchQueueItemDto: {
            canDispatch: boolean;
            lines: components["schemas"]["ShipmentPackageDto"][];
            preparationId: components["schemas"]["LongId"];
            preparationRevision: string;
            source: components["schemas"]["FulfillmentOrderDto"];
        };
        DispatchRecoveryDto: {
            result: null | components["schemas"]["DispatchResultDto"];
            status: string;
        };
        DispatchReservedLotDto: {
            definitionId: components["schemas"]["LongId"];
            /** Format: date */
            expiresOn: null | string;
            lotId: components["schemas"]["LongId"];
            /** Format: int32 */
            remainingPackages: number | string;
            reservationId: components["schemas"]["LongId"];
            skuId: components["schemas"]["LongId"];
        };
        DispatchResultDto: {
            number: string;
            numberChannel: components["schemas"]["LongId"];
            numberEntity: components["schemas"]["LongId"];
            numberYear: string;
            orderId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
            shipmentId: components["schemas"]["LongId"];
            suborderId: components["schemas"]["LongId"];
        };
        DispatchReviewDto: {
            courierId: components["schemas"]["LongId"];
            courierName: string;
            preparation: components["schemas"]["DispatchPreparationDto"];
            review: string;
            selected: components["schemas"]["ShipmentLotDto"][];
        };
        DispatchSelectionDto: {
            courierId: components["schemas"]["LongId"];
            lots: components["schemas"]["ShipmentLotDto"][];
            preparationId: components["schemas"]["LongId"];
            preparationRevision: string;
        };
        ExtractionProvenance: {
            engineVersion: string;
            modelSha256: string;
            modelVersion: string;
            preprocessingVersion: string;
            runtimeSha256: string;
        };
        FulfillmentOrderDto: {
            buyerName: string;
            canPrepare: boolean;
            delivery: components["schemas"]["OrderDeliveryDto"];
            lines: components["schemas"]["FulfillmentOrderLineDto"][];
            /** Format: date-time */
            orderedAt: string;
            orderId: components["schemas"]["LongId"];
            review: string;
            revision: string;
            suborderId: components["schemas"]["LongId"];
        };
        FulfillmentOrderLineDto: {
            definition: components["schemas"]["CatalogDefinitionDto"];
            /** Format: int32 */
            orderedPackages: number | string;
            productName: string;
            publicId: string;
            /** Format: int32 */
            remainingPackages: number | string;
            skuCode: string;
            skuId: components["schemas"]["LongId"];
        };
        FulfillmentOrdersDto: {
            hasMore: boolean;
            items: components["schemas"]["FulfillmentOrderDto"][];
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
        InvoiceCandidate: {
            /** Format: int32 */
            acceptedLines: number | string;
            buyerName: string;
            invoiced: boolean;
            receiptId: components["schemas"]["LongId"];
            /** Format: date-time */
            receivedAt: string;
            shipmentId: components["schemas"]["LongId"];
        };
        InvoiceCandidatePage: {
            items: components["schemas"]["InvoiceCandidate"][];
            nextShipmentId: null | components["schemas"]["LongId"];
        };
        InvoiceDto: {
            detail: components["schemas"]["InvoicePreviewDto"];
            documentHash: string;
            /** Format: int32 */
            documentLength: number | string;
            id: components["schemas"]["LongId"];
            /** Format: date-time */
            issuedAt: string;
            number: string;
            numberChannel: components["schemas"]["LongId"];
            numberEntity: components["schemas"]["LongId"];
            /** Format: int32 */
            numberYear: number | string;
        };
        InvoiceIssueDto: {
            reason: string;
            receipts: components["schemas"]["InvoiceReceiptDto"][];
            review: string;
        };
        InvoiceLineDto: {
            acceptedBaseUnits: string;
            baseUnitCode: string;
            /** Format: int32 */
            baseUnitsPerPackage: number | string;
            charges: components["schemas"]["Money"];
            net: components["schemas"]["Money"];
            orderId: components["schemas"]["LongId"];
            packageLabel: string;
            productName: string;
            receiptId: components["schemas"]["LongId"];
            shipmentId: components["schemas"]["LongId"];
            skuCode: string;
            skuId: components["schemas"]["LongId"];
            tax: components["schemas"]["Money"];
        };
        InvoicePageDto: {
            hasMore: boolean;
            items: components["schemas"]["InvoiceDto"][];
        };
        InvoicePreviewDto: {
            buyerName: string;
            charges: components["schemas"]["Money"];
            dayRule: string;
            /** Format: date */
            dueDate: string;
            grouping: string;
            /** Format: date */
            issueDate: string;
            lines: components["schemas"]["InvoiceLineDto"][];
            net: components["schemas"]["Money"];
            /** Format: date */
            periodFrom: string;
            /** Format: date */
            periodTo: string;
            review: string;
            sellerName: string;
            tax: components["schemas"]["Money"];
            taxDocumentProcess: string;
            /** Format: int32 */
            termDays: number | string;
            termOrigin: string;
            total: components["schemas"]["Money"];
        };
        InvoicePreviewRequestDto: {
            purpose: string;
            receipts: components["schemas"]["InvoiceReceiptDto"][];
        };
        InvoiceQueryDto: {
            beforeId: null | components["schemas"]["LongId"];
            purpose: string;
        };
        InvoiceReceiptDto: {
            receiptId: components["schemas"]["LongId"];
            shipmentId: components["schemas"]["LongId"];
        };
        InvoiceRecoveryDto: {
            invoice: null | components["schemas"]["InvoiceDto"];
            status: string;
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
        /** @description Nonnegative decimal string from 0.00 through 999999999999999.99, with at most two fractional digits; responses use exactly two. JSON numbers are invalid. */
        Money: string;
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
        OrderApprovalProgressDto: {
            acceptedSuborders: unknown[];
            cancelled: boolean;
            rejectedSuborders: unknown[];
            revision: string;
            spendingState: string;
        };
        OrderCheckoutDto: {
            input: components["schemas"]["OrderDraftInputDto"];
            purpose: string;
        };
        OrderCheckoutReviewDto: {
            /** Format: date-time */
            calculatedAt: string;
            catalogSubtotal: components["schemas"]["Money"];
            charges: components["schemas"]["Money"];
            currency: string;
            input: components["schemas"]["OrderDraftInputDto"];
            lines: components["schemas"]["OrderQuoteLineDto"][];
            review: string;
            spendingState: string;
            tax: components["schemas"]["Money"];
            total: components["schemas"]["Money"];
        };
        OrderContextDto: {
            branchId: components["schemas"]["LongId"];
            divisionId: null | components["schemas"]["LongId"];
            organizationId: components["schemas"]["LongId"];
        };
        OrderContextOptionDto: {
            branchName: null | string;
            context: components["schemas"]["OrderContextDto"];
            divisionName: null | string;
            organizationName: null | string;
        };
        OrderContextsDto: {
            items: components["schemas"]["OrderContextOptionDto"][];
        };
        OrderDecisionDto: {
            action: string;
            reason: string;
            review: string;
            stage: string;
            suborderId: null | components["schemas"]["LongId"];
        };
        OrderDecisionReceiptDto: {
            result: null | components["schemas"]["OrderDecisionResultDto"];
            status: string;
        };
        OrderDecisionRecoveryDto: {
            action: string;
            key: string;
            purpose: string;
            stage: string;
        };
        OrderDecisionResultDto: {
            action: string;
            decisionId: components["schemas"]["LongId"];
            orderId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
            stage: string;
            state: string;
            suborderId: null | components["schemas"]["LongId"];
        };
        OrderDeliveryDto: {
            address: string;
            city: string;
            instructions: null | string;
            phone: string;
            postalCode: null | string;
            recipientName: string;
        };
        OrderDraftDto: {
            /** Format: date-time */
            createdAt: string;
            id: components["schemas"]["LongId"];
            input: components["schemas"]["OrderDraftInputDto"];
            revision: string;
            /** Format: date-time */
            updatedAt: string;
        };
        OrderDraftInputDto: {
            context: components["schemas"]["OrderContextDto"];
            delivery: null | components["schemas"]["OrderDeliveryDto"];
            purchaseReference: null | string;
            selections: components["schemas"]["OrderPackageSelectionDto"][];
        };
        OrderDraftListDto: {
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            beforeId?: null | string;
            context: components["schemas"]["OrderContextDto"];
            /**
             * Format: int32
             * @default 25
             */
            limit: number;
            purpose: string;
        };
        OrderDraftPageDto: {
            hasMore: boolean;
            items: components["schemas"]["OrderDraftDto"][];
        };
        OrderDraftReceiptDto: {
            result: null | components["schemas"]["OrderDraftResultDto"];
            status: string;
        };
        OrderDraftRecoveryDto: {
            key: string;
            purpose: string;
        };
        OrderDraftResultDto: {
            draftId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
            versionId: components["schemas"]["LongId"];
        };
        OrderDraftSaveDto: {
            draftId: null | components["schemas"]["LongId"];
            input: components["schemas"]["OrderDraftInputDto"];
            reason: string;
        };
        OrderPackageSelectionDto: {
            definitionId: components["schemas"]["LongId"];
            definitionVersion: string;
            /** Format: int32 */
            packages: number;
            priceId: components["schemas"]["LongId"];
            priceVersion: string;
            skuId: components["schemas"]["LongId"];
        };
        OrderQuoteDto: {
            /** Format: date-time */
            calculatedAt: string;
            catalogSubtotal: components["schemas"]["Money"];
            context: components["schemas"]["OrderContextDto"];
            currency: string;
            lines: components["schemas"]["OrderQuoteLineDto"][];
        };
        OrderQuoteLineDto: {
            baseUnits: string;
            catalogSubtotal: components["schemas"]["Money"];
            definition: components["schemas"]["CatalogDefinitionDto"];
            familyId: components["schemas"]["LongId"];
            familyName: string;
            /** Format: int32 */
            packages: number | string;
            price: components["schemas"]["CatalogPriceDto"];
            publicId: string;
            skuCode: string;
        };
        OrderQuoteRequestDto: {
            context: components["schemas"]["OrderContextDto"];
            purpose: string;
            selections: components["schemas"]["OrderPackageSelectionDto"][];
        };
        OrderReadDto: {
            purpose: string;
        };
        OrderReviewDto: {
            buyerName: string;
            cancelled: boolean;
            canDecide: boolean;
            catalogSubtotal: components["schemas"]["Money"];
            charges: null | components["schemas"]["Money"];
            context: components["schemas"]["OrderContextDto"];
            currency: string;
            delivery: components["schemas"]["OrderDeliveryDto"];
            lines: components["schemas"]["OrderQuoteLineDto"][];
            /** Format: date-time */
            orderedAt: string;
            orderId: components["schemas"]["LongId"];
            review: string;
            revision: string;
            spendingState: string;
            stage: string;
            storeState: null | string;
            suborderId: null | components["schemas"]["LongId"];
            tax: null | components["schemas"]["Money"];
            total: null | components["schemas"]["Money"];
        };
        OrderReviewListDto: {
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            beforeId?: null | string;
            purpose: string;
            stage: string;
        };
        OrderReviewPageDto: {
            hasMore: boolean;
            items: components["schemas"]["OrderReviewDto"][];
        };
        OrderReviewReadDto: {
            purpose: string;
            stage: string;
            suborderId: null | components["schemas"]["LongId"];
        };
        OrderSubmitDto: {
            input: components["schemas"]["OrderDraftInputDto"];
            reason: string;
            review: string;
        };
        OrderSubmitRecoveryDto: {
            result: null | components["schemas"]["OrderSubmitResultDto"];
            status: string;
        };
        OrderSubmitResultDto: {
            orderId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
            spendingState: string;
            suborderIds: unknown[];
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
        OwnOrderListDto: {
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            beforeId?: null | string;
            context?: null | components["schemas"]["OrderContextDto"];
            /**
             * Format: int32
             * @default 25
             */
            limit: number | string;
            purpose: string;
        };
        PaymentCorrectionBindingDto: {
            declarationRevision: string;
            evidence: components["schemas"]["PaymentEvidenceReferenceDto"];
            extractionId: components["schemas"]["LongId"];
            submissionId: components["schemas"]["LongId"];
        };
        PaymentCorrectionFieldDto: {
            field: string;
            value: null | string;
        };
        PaymentCorrectionQueryDto: {
            binding: components["schemas"]["PaymentCorrectionBindingDto"];
            purpose: string;
        };
        PaymentCorrectionRecoveryDto: {
            result: null | components["schemas"]["PaymentCorrectionResultDto"];
            status: string;
        };
        PaymentCorrectionResultDto: {
            correctionId: components["schemas"]["LongId"];
            declarationId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            reviewRevision: string;
            submissionId: components["schemas"]["LongId"];
        };
        PaymentCorrectionSaveDto: {
            binding: components["schemas"]["PaymentCorrectionBindingDto"];
            fields: components["schemas"]["PaymentCorrectionFieldDto"][];
            reason: string;
        };
        PaymentCorrectionVersionDto: {
            actorId: components["schemas"]["LongId"];
            beforeFields: components["schemas"]["PaymentReviewField"][];
            fields: components["schemas"]["PaymentReviewField"][];
            id: components["schemas"]["LongId"];
            reason: string;
            /** Format: date-time */
            recordedAt: string;
            revision: string;
        };
        PaymentCorrectionViewDto: {
            binding: components["schemas"]["PaymentCorrectionBindingDto"];
            declarationId: components["schemas"]["LongId"];
            fields: components["schemas"]["PaymentReviewField"][];
            originalFields: components["schemas"]["PaymentReviewField"][];
            recent: components["schemas"]["PaymentCorrectionVersionDto"][];
            reviewRevision: string;
        };
        PaymentDraftDto: {
            /** Format: date-time */
            createdAt: string;
            id: components["schemas"]["LongId"];
            input: components["schemas"]["PaymentDraftInputDto"];
            invoices: components["schemas"]["PaymentInvoiceReference"][];
            revision: string;
            state: string;
            submission: null | components["schemas"]["PaymentSubmissionSummaryDto"];
            /** Format: date-time */
            updatedAt: string;
        };
        PaymentDraftInputDto: {
            bankReference: null | string;
            declaredAmount: components["schemas"]["Money"];
            invoiceIds: unknown[];
            note: null | string;
            payerName: string;
            /** Format: date */
            transferDate: string;
        };
        PaymentDraftPageDto: {
            hasMore: boolean;
            items: components["schemas"]["PaymentDraftDto"][];
        };
        PaymentDraftRecoveryDto: {
            result: null | components["schemas"]["PaymentDraftResultDto"];
            status: string;
        };
        PaymentDraftResultDto: {
            declarationId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
            versionId: components["schemas"]["LongId"];
        };
        PaymentDraftSaveDto: {
            declarationId: null | components["schemas"]["LongId"];
            input: components["schemas"]["PaymentDraftInputDto"];
            reason: string;
        };
        PaymentEvidencePageDto: {
            items: components["schemas"]["PaymentEvidenceStatusDto"][];
        };
        PaymentEvidenceReadDto: {
            purpose: string;
            reference: components["schemas"]["PaymentEvidenceReferenceDto"];
        };
        PaymentEvidenceRecoverDto: {
            purpose: string;
            reason: string;
        };
        PaymentEvidenceReferenceDto: {
            id: components["schemas"]["LongId"];
            sha256: string;
            version: string;
        };
        PaymentEvidenceReserveDto: {
            reason: string;
        };
        PaymentEvidenceStatusDto: {
            decoderStatus: string;
            /** Format: date-time */
            expiresAt: string;
            reference: null | components["schemas"]["PaymentEvidenceReferenceDto"];
            reservationId: components["schemas"]["LongId"];
            revision: string;
            safeFailure: null | string;
            scanStatus: string;
            state: string;
        };
        PaymentInvoiceReference: {
            buyerName: string;
            invoiceId: components["schemas"]["LongId"];
            number: string;
            sellerName: string;
            total: components["schemas"]["Money"];
        };
        PaymentOcrFieldDto: {
            boundingBox: null | string[];
            confidence: null | string;
            field: string;
            flags: string[];
            value: null | string;
        };
        PaymentOcrResultDto: {
            /** Format: date-time */
            extractedAt: string;
            fields: components["schemas"]["PaymentOcrFieldDto"][];
            id: components["schemas"]["LongId"];
            provenance: components["schemas"]["ExtractionProvenance"];
            qualityFlags: string[];
        };
        PaymentOcrViewDto: {
            declarationId: components["schemas"]["LongId"];
            evidence: components["schemas"]["PaymentEvidenceReferenceDto"];
            result: null | components["schemas"]["PaymentOcrResultDto"];
            state: string;
            submissionId: components["schemas"]["LongId"];
        };
        PaymentReviewField: {
            field: string;
            value: null | string;
        };
        PaymentSubmissionDto: {
            evidence: components["schemas"]["PaymentEvidenceReferenceDto"];
            reason: string;
            reservationId: components["schemas"]["LongId"];
        };
        PaymentSubmissionRecoveryDto: {
            result: null | components["schemas"]["PaymentSubmissionResultDto"];
            status: string;
        };
        PaymentSubmissionResultDto: {
            declarationId: components["schemas"]["LongId"];
            declarationRevision: string;
            reviewRevision: string;
            state: string;
            submissionId: components["schemas"]["LongId"];
            /** Format: date-time */
            submittedAt: string;
        };
        PaymentSubmissionSummaryDto: {
            evidence: components["schemas"]["PaymentEvidenceReferenceDto"];
            id: components["schemas"]["LongId"];
            reservationId: components["schemas"]["LongId"];
            reviewRevision: string;
            /** Format: date-time */
            submittedAt: string;
        };
        PaymentUploadOutcomeDto: {
            reference: null | components["schemas"]["PaymentEvidenceReferenceDto"];
            state: string;
        };
        PaymentUploadRecoveryDto: {
            reservation: null | components["schemas"]["PaymentUploadReservationDto"];
            status: string;
        };
        PaymentUploadReservationDto: {
            /** Format: date-time */
            expiresAt: string;
            reservationId: components["schemas"]["LongId"];
            revision: string;
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
        PreparationDto: {
            acceptedSource: components["schemas"]["FulfillmentOrderDto"];
            currentSourceMatches: boolean;
            id: components["schemas"]["LongId"];
            lines: components["schemas"]["ShipmentPackageDto"][];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
        };
        PreparationPageRequest: {
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            beforeId?: null | string;
            /**
             * Format: int32
             * @default 25
             */
            limit: number | string;
            purpose: string;
        };
        PreparationReadDto: {
            preparation: null | components["schemas"]["PreparationDto"];
        };
        PreparationRecoveryDto: {
            result: null | components["schemas"]["PreparationResultDto"];
            status: string;
        };
        PreparationResultDto: {
            orderId: components["schemas"]["LongId"];
            preparationId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            revision: string;
            suborderId: components["schemas"]["LongId"];
            versionId: components["schemas"]["LongId"];
        };
        ReceiptAmountDto: {
            /** Format: int32 */
            looseUnits: number | string;
            /** Format: int32 */
            packages: number | string;
        };
        ReceiptChallengeCommandDto: {
            documentId: components["schemas"]["LongId"];
            reason: string;
            recipientId: components["schemas"]["LongId"];
        };
        ReceiptChallengeReviewDto: {
            token: string;
        };
        ReceiptChallengeStateDto: {
            challenge: null | components["schemas"]["ShipmentReceiptChallengeView"];
            status: string;
        };
        ReceiptConfirmDto: {
            consent: boolean;
            consentVersion: string;
            lines: components["schemas"]["ReceiptDeclarationDto"][];
            reason: string;
            token: string;
        };
        ReceiptDeclarationDto: {
            accepted: components["schemas"]["ReceiptAmountDto"];
            exceptionReason: null | string;
            missing: components["schemas"]["ReceiptAmountDto"];
            rejected: components["schemas"]["ReceiptAmountDto"];
            skuId: components["schemas"]["LongId"];
        };
        ReceiptDto: {
            documentId: components["schemas"]["LongId"];
            hasException: boolean;
            id: components["schemas"]["LongId"];
            lines: components["schemas"]["ReceiptLineDto"][];
            recipientId: components["schemas"]["LongId"];
            recipientName: string;
            /** Format: date-time */
            recordedAt: string;
            shipmentId: components["schemas"]["LongId"];
        };
        ReceiptLineDto: {
            accepted: components["schemas"]["ReceiptAmountDto"];
            acceptedBaseUnits: string;
            definitionId: components["schemas"]["LongId"];
            dispatchedBaseUnits: string;
            exceptionReason: null | string;
            missing: components["schemas"]["ReceiptAmountDto"];
            missingBaseUnits: string;
            rejected: components["schemas"]["ReceiptAmountDto"];
            rejectedBaseUnits: string;
            skuId: components["schemas"]["LongId"];
        };
        ReceiptProofCommandDto: {
            asRecipient: boolean;
            reason: string;
            receiptId: components["schemas"]["LongId"];
        };
        ReceiptProofDto: {
            byteLength: string;
            contentHash: string;
            id: components["schemas"]["LongId"];
            issuedAsRecipient: boolean;
            /** Format: date-time */
            issuedAt: string;
            receiptId: components["schemas"]["LongId"];
            shipmentId: components["schemas"]["LongId"];
            shipmentRevision: string;
            version: string;
        };
        ReceiptProofReadDto: {
            asRecipient: boolean;
            purpose: string;
        };
        ReceiptProofStateDto: {
            document: null | components["schemas"]["ReceiptProofDto"];
            status: string;
        };
        ReceiptRecipientQueryDto: {
            beforeId: null | string;
            purpose: string;
        };
        ReceiptReviewDto: {
            challengeId: components["schemas"]["LongId"];
            consentText: string;
            consentVersion: string;
            documentHash: string;
            documentId: components["schemas"]["LongId"];
            documentVersion: string;
            /** Format: date-time */
            expiresAt: string;
            lines: components["schemas"]["ShipmentReceiptReviewLine"][];
            recipientName: string;
            /** Format: date-time */
            serverTime: string;
            shipmentId: components["schemas"]["LongId"];
        };
        ReceiptStateDto: {
            receipt: null | components["schemas"]["ReceiptDto"];
            status: string;
        };
        ReceivingAccountDecisionDto: {
            decision: string;
            observationId: null | components["schemas"]["LongId"];
            reason: string;
        };
        ReceivingAccountDetailsDto: {
            accountNumber: string;
            bankCode: string;
            currency: string;
            holderName: string;
        };
        ReceivingAccountObserveDto: {
            attested: boolean;
            details: components["schemas"]["ReceivingAccountDetailsDto"];
            reason: string;
            sourceReference: string;
        };
        ReceivingAccountProposeDto: {
            accountId: null | components["schemas"]["LongId"];
            details: components["schemas"]["ReceivingAccountDetailsDto"];
            policyId: components["schemas"]["LongId"];
            policyRevision: string;
            reason: string;
        };
        ReceivingAccountQueryDto: {
            afterId: null | components["schemas"]["LongId"];
            purpose: string;
        };
        ReceivingAccountReadDto: {
            purpose: string;
        };
        ReceivingAccountRecoveryDto: {
            key: string;
            operation: string;
            purpose: string;
        };
        ReceivingDecisionDto: {
            accountVersion: null | string;
            actorId: components["schemas"]["LongId"];
            decision: string;
            id: components["schemas"]["LongId"];
            observationId: null | components["schemas"]["LongId"];
            reason: string;
            /** Format: date-time */
            recordedAt: string;
            versionId: null | components["schemas"]["LongId"];
        };
        ReceivingObservationDto: {
            actorId: components["schemas"]["LongId"];
            /** Format: date-time */
            checkedAt: string;
            details: components["schemas"]["ReceivingAccountDetailsDto"];
            id: components["schemas"]["LongId"];
            reason: string;
            sourceReference: string;
        };
        ReceivingPageDto: {
            hasMore: boolean;
            items: components["schemas"]["ReceivingSummaryDto"][];
        };
        ReceivingPolicyDto: {
            active: boolean;
            approvalReference: string;
            /** Format: date-time */
            approvedAt: string;
            bankCode: string;
            bankName: string;
            /** Format: date-time */
            expiresAt: string;
            id: components["schemas"]["LongId"];
            maxObservationAgeSeconds: string;
            method: string;
            operatingOwnerReference: string;
            revision: string;
            scope: components["schemas"]["ReceivingScopeDto"];
            trustedChannelReference: string;
        };
        ReceivingPolicyPageDto: {
            hasMore: boolean;
            items: components["schemas"]["ReceivingPolicyDto"][];
        };
        ReceivingProposalDto: {
            accountId: components["schemas"]["LongId"];
            actorId: components["schemas"]["LongId"];
            baseVersion: string;
            details: components["schemas"]["ReceivingAccountDetailsDto"];
            id: components["schemas"]["LongId"];
            policy: components["schemas"]["ReceivingPolicyDto"];
            reason: string;
            /** Format: date-time */
            recordedAt: string;
        };
        ReceivingRecoveryResultDto: {
            result: null | components["schemas"]["ReceivingResultDto"];
            status: string;
        };
        ReceivingResultDto: {
            accountId: components["schemas"]["LongId"];
            accountVersion: null | string;
            operation: string;
            proposalId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            recordId: components["schemas"]["LongId"];
        };
        ReceivingScopeDto: {
            channelId: components["schemas"]["LongId"];
            fulfillmentOrganizationId: components["schemas"]["LongId"];
            shopId: components["schemas"]["LongId"];
            warehouseId: null | components["schemas"]["LongId"];
        };
        ReceivingSummaryDto: {
            accountId: components["schemas"]["LongId"];
            actorId: components["schemas"]["LongId"];
            bankCode: string;
            baseVersion: string;
            currentVersion: string;
            maskedAccount: string;
            proposalId: components["schemas"]["LongId"];
            /** Format: date-time */
            recordedAt: string;
            scope: components["schemas"]["ReceivingScopeDto"];
            state: string;
        };
        ReceivingVersionDto: {
            details: components["schemas"]["ReceivingAccountDetailsDto"];
            /** Format: date-time */
            effectiveAt: string;
            id: components["schemas"]["LongId"];
            previousVersionId: null | components["schemas"]["LongId"];
            version: string;
        };
        ReceivingViewDto: {
            currentVersion: null | components["schemas"]["ReceivingVersionDto"];
            decision: null | components["schemas"]["ReceivingDecisionDto"];
            observation: null | components["schemas"]["ReceivingObservationDto"];
            proposal: components["schemas"]["ReceivingProposalDto"];
        };
        SavePreparationDto: {
            lines: components["schemas"]["ShipmentPackageDto"][];
            reason: string;
            review: string;
        };
        /** @enum {unknown} */
        ScopeNodeKind: "buyingOrganization" | "fulfillmentOrganization" | "branch" | "division" | "shop" | "warehouse" | "channel";
        ShipmentDocumentCommandDto: {
            reason: string;
        };
        ShipmentDocumentDto: {
            byteLength: string;
            contentHash: string;
            id: components["schemas"]["LongId"];
            /** Format: date-time */
            issuedAt: string;
            shipmentId: components["schemas"]["LongId"];
            shipmentRevision: string;
            version: string;
        };
        ShipmentDocumentStateDto: {
            document: null | components["schemas"]["ShipmentDocumentDto"];
            status: string;
        };
        ShipmentLotDto: {
            /** Format: int32 */
            packages: number;
            reservationId: components["schemas"]["LongId"];
        };
        ShipmentPackageDto: {
            /** Format: int32 */
            packages: number;
            skuId: components["schemas"]["LongId"];
        };
        ShipmentReadDto: {
            buyerName: string;
            courierId: components["schemas"]["LongId"];
            courierName: string;
            delivery: components["schemas"]["OrderDeliveryDto"];
            /** Format: date-time */
            dispatchedAt: string;
            id: components["schemas"]["LongId"];
            lines: components["schemas"]["ShipmentReadLineDto"][];
            number: string;
            numberChannel: components["schemas"]["LongId"];
            numberEntity: components["schemas"]["LongId"];
            numberYear: string;
            orderId: components["schemas"]["LongId"];
            revision: string;
            suborderId: components["schemas"]["LongId"];
        };
        ShipmentReadLineDto: {
            baseUnits: string;
            definition: components["schemas"]["CatalogDefinitionDto"];
            /** Format: int32 */
            packages: number | string;
            productName: string;
            skuCode: string;
            skuId: components["schemas"]["LongId"];
        };
        ShipmentReadPageDto: {
            hasMore: boolean;
            items: components["schemas"]["ShipmentReadDto"][];
        };
        ShipmentReceiptChallengeView: {
            documentId: components["schemas"]["LongId"];
            /** Format: date-time */
            expiresAt: string;
            id: components["schemas"]["LongId"];
            recipientId: components["schemas"]["LongId"];
            recipientName: string;
            /** Format: date-time */
            serverTime: string;
            shipmentId: components["schemas"]["LongId"];
            token: null | string;
        };
        ShipmentReceiptReviewLine: {
            baseUnitCode: string;
            /** Format: int32 */
            baseUnitsPerPackage: number | string;
            definitionId: components["schemas"]["LongId"];
            packageLabel: string;
            /** Format: int32 */
            packages: number | string;
            productName: string;
            skuCode: string;
            skuId: components["schemas"]["LongId"];
        };
        ShipmentRecipientPage: {
            hasMore: boolean;
            items: components["schemas"]["ShipmentRecipientView"][];
        };
        ShipmentRecipientView: {
            id: components["schemas"]["LongId"];
            name: string;
        };
        StatementAccountDto: {
            accountId: components["schemas"]["LongId"];
            details: components["schemas"]["ReceivingAccountDetailsDto"];
            /** Format: date-time */
            effectiveAt: string;
            version: string;
            versionId: components["schemas"]["LongId"];
        };
        StatementAccountsDto: {
            hasMore: boolean;
            items: components["schemas"]["StatementAccountDto"][];
        };
        StatementAccountsQueryDto: {
            afterId: null | components["schemas"]["LongId"];
            profileId: components["schemas"]["LongId"];
            profileRevision: string;
            purpose: string;
        };
        StatementDetailDto: {
            account: components["schemas"]["StatementAccountDto"];
            actorId: components["schemas"]["LongId"];
            filename: string;
            importId: components["schemas"]["LongId"];
            preview: components["schemas"]["StatementParsedDto"];
            profile: components["schemas"]["StatementProfileDto"];
            reason: string;
            /** Format: date-time */
            recordedAt: string;
            sourceReference: string;
            state: string;
        };
        StatementFormatDto: {
            columns: string[];
            creditMarker: string;
            dateFormat: string;
            debitMarker: string;
            decimalSeparator: string;
            delimiter: string;
            identityNamespace: null | string;
        };
        StatementImportsDto: {
            hasMore: boolean;
            items: components["schemas"]["StatementSummaryDto"][];
        };
        StatementIssueDto: {
            code: string;
            field: string;
            /** Format: int32 */
            row: number | string;
        };
        StatementParsedDto: {
            canStage: boolean;
            credits: components["schemas"]["Money"];
            debits: components["schemas"]["Money"];
            issues: components["schemas"]["StatementIssueDto"][];
            rows: components["schemas"]["StatementRowDto"][];
            sourceSha256: string;
        };
        StatementPreviewDto: {
            originalBase64: string;
            selection: components["schemas"]["StatementSelectionDto"];
        };
        StatementPreviewResultDto: {
            binding: string;
            preview: components["schemas"]["StatementParsedDto"];
        };
        StatementProfileDto: {
            active: boolean;
            approvalReference: string;
            /** Format: date-time */
            approvedAt: string;
            bankCode: string;
            /** Format: date-time */
            expiresAt: string;
            format: components["schemas"]["StatementFormatDto"];
            id: components["schemas"]["LongId"];
            name: string;
            operatingOwnerReference: string;
            revision: string;
            trustedSourceReference: string;
        };
        StatementProfilesDto: {
            hasMore: boolean;
            items: components["schemas"]["StatementProfileDto"][];
        };
        StatementRecoveryDto: {
            result: null | components["schemas"]["StatementResultDto"];
            status: string;
        };
        StatementResultDto: {
            importId: components["schemas"]["LongId"];
            operation: string;
            /** Format: date-time */
            recordedAt: string;
            /** Format: int32 */
            rowCount: number | string;
        };
        StatementRowDto: {
            amount: components["schemas"]["Money"];
            bookedDate: string;
            direction: string;
            identityCandidate: null | string;
            /** Format: int32 */
            number: number | string;
            payerName: null | string;
            reference: null | string;
            transactionId: null | string;
        };
        StatementSelectionDto: {
            accountId: components["schemas"]["LongId"];
            accountVersionId: components["schemas"]["LongId"];
            profileId: components["schemas"]["LongId"];
            profileRevision: string;
        };
        StatementStageDto: {
            attested: boolean;
            binding: string;
            filename: string;
            originalBase64: string;
            reason: string;
            selection: components["schemas"]["StatementSelectionDto"];
            sourceReference: string;
        };
        StatementSummaryDto: {
            accountId: components["schemas"]["LongId"];
            accountVersionId: components["schemas"]["LongId"];
            actorId: components["schemas"]["LongId"];
            credits: components["schemas"]["Money"];
            debits: components["schemas"]["Money"];
            filename: string;
            importId: components["schemas"]["LongId"];
            /** Format: int32 */
            issueCount: number | string;
            maskedAccount: string;
            profileId: components["schemas"]["LongId"];
            profileName: string;
            /** Format: date-time */
            recordedAt: string;
            /** Format: int32 */
            rowCount: number | string;
            state: string;
        };
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
        StockSourceAliasCommandDto: {
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            appliedAliasVersion: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            baseAliasVersion: null | string;
            decisionId: null | components["schemas"]["LongId"];
            physicalPoolId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            proposalId: components["schemas"]["LongId"];
            /** @enum {string} */
            proposalVersion: "1";
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourceAliasCurrentDto: {
            /** @enum {string} */
            bindingState: "absent" | "disabled" | "stale" | "current";
            mapping: null | components["schemas"]["StockSourceAliasDetailDto"];
            requestedLocation: components["schemas"]["AccessScopeDto"];
            source: components["schemas"]["StockSourceCurrentDto"];
        };
        StockSourceAliasDetailDto: {
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            appliedAliasVersion: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            baseAliasVersion: string;
            decision: null | components["schemas"]["StockSourceSetupDecisionDto"];
            enabled: boolean;
            id: components["schemas"]["LongId"];
            ownerLocation: components["schemas"]["AccessScopeDto"];
            physicalKey: string;
            physicalPoolId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            /** @enum {string} */
            proposalVersion: "1";
            /** Format: date-time */
            proposedAt: string;
            reason: string;
            requestedLocation: components["schemas"]["AccessScopeDto"];
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourceAliasHistoryDto: {
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
            /** @enum {string} */
            purpose: "inventory-source-alias-history";
            requestedLocation: components["schemas"]["AccessScopeDto"];
        };
        StockSourceAliasPageDto: {
            items: components["schemas"]["StockSourceAliasDetailDto"][];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            total: string;
        };
        StockSourceAliasProposeDto: {
            enabled: boolean;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            expectedAliasVersion: string;
            reason: string;
            requestedLocation: components["schemas"]["AccessScopeDto"];
        };
        StockSourceAliasReadDto: {
            /** @enum {string} */
            purpose: "inventory-source-alias";
            requestedLocation: components["schemas"]["AccessScopeDto"];
        };
        StockSourceAliasReceiptDto: {
            result: null | components["schemas"]["StockSourceAliasCommandDto"];
            /** @enum {string} */
            state: "not_confirmed" | "committed";
        };
        StockSourceAliasRecoveryDto: {
            key: string;
            /** @enum {string} */
            operation: "stock.source.alias.propose" | "stock.source.alias.approve" | "stock.source.alias.reject" | "stock.source.alias.withdraw";
            physicalPoolId: components["schemas"]["LongId"];
            /** @enum {string} */
            purpose: "operation-recovery";
            requestedLocation: components["schemas"]["AccessScopeDto"];
        };
        StockSourceCurrentDto: {
            /** Format: date-time */
            approvedAt: string;
            creationProposalId: components["schemas"]["LongId"];
            physicalPoolId: components["schemas"]["LongId"];
            /** @enum {string} */
            poolState: "inactive" | "active";
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            revisionProposalId: null | components["schemas"]["LongId"];
            settings: components["schemas"]["StockSourceSettingsDto"];
        };
        StockSourceCurrentPageDto: {
            items: components["schemas"]["StockSourceCurrentDto"][];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            total: string;
        };
        StockSourceCurrentQueryDto: {
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
            /** @enum {string} */
            purpose: "inventory-source-current";
        };
        StockSourcePolicyBindingDto: {
            configurationHash: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            policyVersion: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            suspensionVersion: string;
        };
        StockSourcePolicyCommandDto: {
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            basePolicyVersion: null | string;
            decisionId: null | components["schemas"]["LongId"];
            physicalPoolId: components["schemas"]["LongId"];
            policyId: null | components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            policyVersion: null | string;
            proposalId: components["schemas"]["LongId"];
            /** @enum {string} */
            proposalVersion: "1";
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourcePolicyConfigurationDto: {
            active: boolean;
            creationProposalId: components["schemas"]["LongId"];
            mappings: components["schemas"]["StockSourcePolicyMappingDto"][];
            revisionProposalId: null | components["schemas"]["LongId"];
            settings: components["schemas"]["StockSourceSettingsDto"];
            /** Format: date-time */
            sourceApprovedAt: string;
        };
        StockSourcePolicyDetailDto: {
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            basePolicyVersion: string;
            configuration: components["schemas"]["StockSourcePolicyConfigurationDto"];
            configurationHash: string;
            decision: null | components["schemas"]["StockSourceSetupDecisionDto"];
            decisionId: null | components["schemas"]["LongId"];
            id: components["schemas"]["LongId"];
            input: components["schemas"]["StockSourcePolicyInputDto"];
            physicalPoolId: components["schemas"]["LongId"];
            policyId: null | components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            policyVersion: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            /** @enum {string} */
            proposalVersion: "1";
            /** Format: date-time */
            proposedAt: string;
            reason: string;
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            suspensionVersion: string;
        };
        StockSourcePolicyHistoryDto: {
            /** @default 25 */
            limit: number;
            /** @default 0 */
            offset: number;
            /** @enum {string} */
            purpose: "inventory-source-policy-history";
        };
        StockSourcePolicyInputDto: {
            externalTenant: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            feedIntervalMilliseconds: string;
            outageProcedureReference: string;
            reconciliationProcedureReference: string;
            /** @enum {string} */
            writer: "unitToko" | "externalSystem";
        };
        StockSourcePolicyLatestDto: {
            bindingCurrent: boolean;
            policy: null | components["schemas"]["StockSourcePolicyDetailDto"];
        };
        StockSourcePolicyMappingDto: {
            aliasId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            aliasVersion: string;
            decisionId: components["schemas"]["LongId"];
            enabled: boolean;
            location: components["schemas"]["AccessScopeDto"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            proposalId: components["schemas"]["LongId"];
        };
        StockSourcePolicyPageDto: {
            items: components["schemas"]["StockSourcePolicyDetailDto"][];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            total: string;
        };
        StockSourcePolicyPreviewDto: {
            configuration: components["schemas"]["StockSourcePolicyConfigurationDto"];
            configurationHash: string;
            physicalPoolId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            policyVersion: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            suspensionVersion: string;
        };
        StockSourcePolicyPreviewRequestDto: {
            /** @enum {string} */
            purpose: "inventory-source-policy-preview";
        };
        StockSourcePolicyProposeDto: {
            configurationHash: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            expectedPolicyVersion: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            expectedSuspensionVersion: string;
            input: components["schemas"]["StockSourcePolicyInputDto"];
            reason: string;
        };
        StockSourcePolicyReadDto: {
            /** @enum {string} */
            purpose: "inventory-source-policy-history";
        };
        StockSourcePolicyReceiptDto: {
            result: null | components["schemas"]["StockSourcePolicyCommandDto"];
            /** @enum {string} */
            state: "not_confirmed" | "committed";
        };
        StockSourcePolicyRecoveryDto: {
            key: string;
            /** @enum {string} */
            operation: "stock.source.policy.propose" | "stock.source.policy.approve" | "stock.source.policy.reject" | "stock.source.policy.withdraw";
            physicalPoolId: components["schemas"]["LongId"];
            /** @enum {string} */
            purpose: "operation-recovery";
        };
        StockSourcePolicyReviewDto: {
            /** @enum {string} */
            action: "approve" | "reject" | "withdraw";
            bindingCurrent: boolean;
            current: components["schemas"]["StockSourcePolicyBindingDto"];
            proposal: components["schemas"]["StockSourcePolicyDetailDto"];
        };
        StockSourcePolicyReviewRequestDto: {
            /** @enum {string} */
            action: "approve" | "reject" | "withdraw";
            /** @enum {string} */
            purpose: "inventory-source-policy-review";
        };
        StockSourceRevisionCommandDto: {
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            appliedPoolVersion: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            basePoolVersion: null | string;
            decisionId: null | components["schemas"]["LongId"];
            physicalPoolId: components["schemas"]["LongId"];
            proposalId: components["schemas"]["LongId"];
            /** @enum {string} */
            proposalVersion: "1";
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourceRevisionDetailDto: {
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            appliedPoolVersion: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            basePoolVersion: string;
            baseSettings: components["schemas"]["StockSourceSettingsDto"];
            decision: null | components["schemas"]["StockSourceSetupDecisionDto"];
            id: components["schemas"]["LongId"];
            physicalPoolId: components["schemas"]["LongId"];
            /** @enum {string} */
            proposalVersion: "1";
            /** Format: date-time */
            proposedAt: string;
            proposedSettings: components["schemas"]["StockSourceSettingsDto"];
            reason: string;
            /** @enum {string} */
            status: "proposed" | "approved" | "rejected" | "withdrawn";
        };
        StockSourceRevisionPageDto: {
            items: components["schemas"]["StockSourceRevisionDetailDto"][];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            total: string;
        };
        StockSourceRevisionQueryDto: {
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
            /** @description Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid. */
            physicalPoolId?: null | string;
            /** @enum {string} */
            purpose: "inventory-source-revisions";
        };
        StockSourceRevisionReadDto: {
            location: components["schemas"]["AccessScopeDto"];
            /** @enum {string} */
            purpose: "inventory-source-revisions";
        };
        StockSourceRevisionReceiptDto: {
            result: null | components["schemas"]["StockSourceRevisionCommandDto"];
            /** @enum {string} */
            state: "not_confirmed" | "committed";
        };
        StockSourceRevisionRecoveryDto: {
            key: string;
            location: components["schemas"]["AccessScopeDto"];
            /** @enum {string} */
            operation: "stock.source.revision.propose" | "stock.source.revision.approve" | "stock.source.revision.reject" | "stock.source.revision.withdraw";
            /** @enum {string} */
            purpose: "operation-recovery";
        };
        StockSourceSettingsDto: {
            /** @enum {string} */
            control: "exclusiveWebAllocation" | "sharedWebLedger" | "externalHold";
            discrepancyOwnerReference: string;
            externalNamespace: null | string;
            handoffReference: string;
            label: string;
            location: components["schemas"]["AccessScopeDto"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            maximumAgeMilliseconds: string;
            /** @enum {string} */
            mode: "webLedger" | "externalLedger";
            physicalKey: string;
            reservationProtocolReference: null | string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            safetyBufferBaseUnits: string;
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
        StockSourceSuspensionCommandDto: {
            physicalPoolId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            /** @enum {string} */
            status: "suspended";
            suspensionId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            suspensionVersion: string;
        };
        StockSourceSuspensionDetailDto: {
            affectedLocations: components["schemas"]["AccessScopeDto"][];
            id: components["schemas"]["LongId"];
            ownerLocation: components["schemas"]["AccessScopeDto"];
            physicalKey: string;
            physicalPoolId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            reason: string;
            /** Format: date-time */
            suspendedAt: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            suspensionVersion: string;
            wasActive: boolean;
        };
        StockSourceSuspensionHistoryDto: {
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
            /** @enum {string} */
            purpose: "inventory-source-suspension-history";
        };
        StockSourceSuspensionPageDto: {
            items: components["schemas"]["StockSourceSuspensionDetailDto"][];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            total: string;
        };
        StockSourceSuspensionPreviewDto: {
            active: boolean;
            affectedLocations: components["schemas"]["AccessScopeDto"][];
            configurationHash: string;
            physicalPoolId: components["schemas"]["LongId"];
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            poolVersion: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            suspensionVersion: string;
        };
        StockSourceSuspensionPreviewRequestDto: {
            /** @enum {string} */
            purpose: "inventory-source-suspension-preview";
        };
        StockSourceSuspensionReadDto: {
            /** @enum {string} */
            purpose: "inventory-source-suspension-history";
        };
        StockSourceSuspensionReceiptDto: {
            result: null | components["schemas"]["StockSourceSuspensionCommandDto"];
            /** @enum {string} */
            state: "not_confirmed" | "committed";
        };
        StockSourceSuspensionRecoveryDto: {
            key: string;
            /** @enum {string} */
            operation: "stock.source.suspend";
            physicalPoolId: components["schemas"]["LongId"];
            /** @enum {string} */
            purpose: "operation-recovery";
        };
        StockSourceSuspensionRequestDto: {
            configurationHash: string;
            /** @description Exact decimal Int64 string; runtime range and workflow version rules also apply. */
            expectedSuspensionVersion: string;
            reason: string;
        };
        StockSourceWarehouseDto: {
            channelName: string;
            location: components["schemas"]["AccessScopeDto"];
            organizationName: string;
            shopName: string;
            warehouseName: string;
        };
        StockSourceWarehousePageDto: {
            items: components["schemas"]["StockSourceWarehouseDto"][];
        };
        StockSourceWarehouseQueryDto: {
            /** @enum {string} */
            purpose: "inventory-source-propose" | "inventory-source-review" | "inventory-source-history";
        };
        SubmittedOrderDto: {
            buyerName: string;
            catalogSubtotal: components["schemas"]["Money"];
            charges: components["schemas"]["Money"];
            currency: string;
            input: components["schemas"]["OrderDraftInputDto"];
            lines: components["schemas"]["OrderQuoteLineDto"][];
            progress: components["schemas"]["OrderApprovalProgressDto"];
            submission: components["schemas"]["OrderSubmitResultDto"];
            tax: components["schemas"]["Money"];
            total: components["schemas"]["Money"];
        };
        SubmittedOrderPageDto: {
            hasMore: boolean;
            items: components["schemas"]["SubmittedOrderDto"][];
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
    ReadBankReceipt: {
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
                "application/json": components["schemas"]["ReceivingAccountReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BankReceiptViewDto"];
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
    ConfirmBankReceipt: {
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
                "application/json": components["schemas"]["BankReceiptConfirmDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BankReceiptResultDto"];
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
    ReadBankReceiptImportConfirmations: {
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
                "application/json": components["schemas"]["ReceivingAccountReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BankReceiptImportDto"];
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
    RecoverBankReceiptConfirmation: {
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
                "application/json": components["schemas"]["ReceivingAccountRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BankReceiptRecoveryDto"];
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
    PreviewBankReceiptConfirmation: {
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
                "application/json": components["schemas"]["BankReceiptPreviewInputDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BankReceiptPreviewDto"];
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
    ListBankReceipts: {
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
                "application/json": components["schemas"]["ReceivingAccountQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["BankReceiptListDto"];
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
    ListBankStatementAccounts: {
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
                "application/json": components["schemas"]["StatementAccountsQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementAccountsDto"];
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
    StageBankStatement: {
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
                "application/json": components["schemas"]["StatementStageDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementResultDto"];
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
    ReadBankStatementOriginal: {
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
                "application/json": components["schemas"]["ReceivingAccountReadDto"];
            };
        };
        responses: {
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
    ReadBankStatementImport: {
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
                "application/json": components["schemas"]["ReceivingAccountReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementDetailDto"];
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
    ListBankStatementImports: {
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
                "application/json": components["schemas"]["ReceivingAccountQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementImportsDto"];
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
    RecoverBankStatementImport: {
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
                "application/json": components["schemas"]["ReceivingAccountRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementRecoveryDto"];
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
    PreviewBankStatement: {
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
                "application/json": components["schemas"]["StatementPreviewDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementPreviewResultDto"];
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
    ListBankStatementProfiles: {
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
                "application/json": components["schemas"]["ReceivingAccountQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StatementProfilesDto"];
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
    IssueInvoice: {
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
                "application/json": components["schemas"]["InvoiceIssueDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["InvoiceDto"];
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
            /** @description Payload Too Large */
            413: {
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
    DownloadInvoice: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/html": string;
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
            /** @description Payload Too Large */
            413: {
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
    ReadInvoice: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["InvoiceDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListInvoiceCandidates: {
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
                "application/json": components["schemas"]["InvoiceQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["InvoiceCandidatePage"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverInvoice: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["InvoiceRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    PreviewInvoice: {
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
                "application/json": components["schemas"]["InvoicePreviewRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["InvoicePreviewDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListInvoices: {
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
                "application/json": components["schemas"]["InvoiceQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["InvoicePageDto"];
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
            /** @description Payload Too Large */
            413: {
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
    SavePaymentDeclaration: {
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
                "application/json": components["schemas"]["PaymentDraftSaveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentDraftResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReservePaymentEvidence: {
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
                "application/json": components["schemas"]["PaymentEvidenceReserveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentUploadReservationDto"];
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
    UploadPaymentEvidence: {
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
                reservationId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/octet-stream": string;
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentUploadOutcomeDto"];
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
    ReadPaymentEvidenceOriginal: {
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
                reservationId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PaymentEvidenceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "image/jpeg": string;
                    "image/png": string;
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
    ReadPaymentEvidenceStatus: {
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
                reservationId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentEvidenceStatusDto"];
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
    RecoverPaymentEvidenceUpload: {
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
            path: {
                id: string;
                reservationId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PaymentEvidenceRecoverDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentUploadOutcomeDto"];
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
    RecoverPaymentEvidenceReservation: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentUploadRecoveryDto"];
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
    ListPaymentEvidence: {
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
                "application/json": components["schemas"]["InvoiceQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentEvidencePageDto"];
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
    ReadPaymentOcr: {
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
                "application/json": components["schemas"]["PaymentEvidenceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentOcrViewDto"];
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
    ReadPaymentDeclaration: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentDraftDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverPaymentSubmission: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentSubmissionRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    SubmitPaymentDeclaration: {
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
                "application/json": components["schemas"]["PaymentSubmissionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentSubmissionResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverPaymentDeclaration: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentDraftRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListPaymentDeclarations: {
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
                "application/json": components["schemas"]["InvoiceQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentDraftPageDto"];
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
            /** @description Payload Too Large */
            413: {
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
    CorrectPaymentReview: {
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
                "application/json": components["schemas"]["PaymentCorrectionSaveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentCorrectionResultDto"];
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
    RecoverPaymentCorrection: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentCorrectionRecoveryDto"];
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
    ReadPaymentCorrections: {
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
                "application/json": components["schemas"]["PaymentCorrectionQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentCorrectionViewDto"];
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
    ReadPaymentReviewOriginal: {
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
                reservationId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PaymentEvidenceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "image/jpeg": string;
                    "image/png": string;
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
    ReadPaymentReviewOcr: {
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
                "application/json": components["schemas"]["PaymentEvidenceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentOcrViewDto"];
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
    ReadPaymentReview: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentDraftDto"];
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
    ListPaymentReviews: {
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
                "application/json": components["schemas"]["InvoiceQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PaymentDraftPageDto"];
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
    RecoverReceivingAccountOperation: {
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
                "application/json": components["schemas"]["ReceivingAccountRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingRecoveryResultDto"];
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
    ListReceivingAccountPolicies: {
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
                "application/json": components["schemas"]["ReceivingAccountQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingPolicyPageDto"];
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
    ProposeReceivingAccount: {
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
                "application/json": components["schemas"]["ReceivingAccountProposeDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingResultDto"];
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
    DecideReceivingAccount: {
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
                "application/json": components["schemas"]["ReceivingAccountDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingResultDto"];
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
    ObserveReceivingAccount: {
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
                "application/json": components["schemas"]["ReceivingAccountObserveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingResultDto"];
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
    ReadReceivingAccountProposal: {
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
                "application/json": components["schemas"]["ReceivingAccountReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingViewDto"];
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
    ListReceivingAccountProposals: {
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
                "application/json": components["schemas"]["ReceivingAccountQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceivingPageDto"];
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
    ApproveCatalogPrice: {
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
                "application/json": components["schemas"]["CatalogPriceDecideDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceCommandDto"];
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
    RejectCatalogPrice: {
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
                "application/json": components["schemas"]["CatalogPriceDecideDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceCommandDto"];
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
    WithdrawCatalogPrice: {
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
                "application/json": components["schemas"]["CatalogPriceDecideDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceCommandDto"];
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
    CatalogPriceCurrent: {
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
                "application/json": components["schemas"]["CatalogPriceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceCurrentDto"];
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
    CatalogPriceHistory: {
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
                "application/json": components["schemas"]["CatalogPriceHistoryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceHistoryResultDto"];
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
    RecoverCatalogPriceOperation: {
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
                "application/json": components["schemas"]["CatalogPriceRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceReceiptDto"];
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
    CatalogPricePreview: {
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
                "application/json": components["schemas"]["CatalogPriceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPricePreviewDto"];
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
    ProposeCatalogPrice: {
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
                "application/json": components["schemas"]["CatalogPriceProposeDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceCommandDto"];
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
    CatalogPriceProposal: {
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
                proposalId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogPriceReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceProposalDto"];
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
    ListCatalogPriceProposals: {
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
                "application/json": components["schemas"]["CatalogPriceProposalListDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceProposalsDto"];
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
    SelectCatalogPriceSkus: {
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
                "application/json": components["schemas"]["CatalogPriceSelectionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogPriceChoicesDto"];
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
    RecordDeliveryAttempt: {
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
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DeliveryAttemptCommandDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DeliveryAttemptDto"];
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
            /** @description Payload Too Large */
            413: {
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
    PreviewDeliveryAttempt: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DeliveryAttemptPreviewDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadDeliveryAttempts: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DeliveryAttemptQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DeliveryAttemptPageDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverDeliveryAttempt: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DeliveryAttemptRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    DispatchShipment: {
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
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DispatchCommandDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DispatchResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListShipmentDispatchCouriers: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PreparationPageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DispatchCouriersDto"];
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
            /** @description Payload Too Large */
            413: {
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
    PreviewShipmentDispatch: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["DispatchPreviewDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DispatchReviewDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipmentDispatchPreparation: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DispatchPreparationDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverShipmentDispatch: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DispatchRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListShipmentDispatchQueue: {
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
                "application/json": components["schemas"]["PreparationPageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["DispatchQueueDto"];
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
            /** @description Payload Too Large */
            413: {
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
    SaveShipmentPreparation: {
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
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SavePreparationDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PreparationResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipmentPreparation: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PreparationReadDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverShipmentPreparation: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PreparationRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipmentPreparationSource: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                suborderId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FulfillmentOrderDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListShipmentPreparationSources: {
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
                "application/json": components["schemas"]["PreparationPageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FulfillmentOrdersDto"];
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
            /** @description Payload Too Large */
            413: {
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
    IssueShipmentReceiptProof: {
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
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptProofCommandDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptProofDto"];
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
            /** @description Payload Too Large */
            413: {
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
    DownloadShipmentReceiptProof: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                documentId: string;
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptProofReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/html": string;
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipmentReceiptProof: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptProofReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptProofStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverShipmentReceiptProof: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptProofStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    IssueShipmentReceiptChallenge: {
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
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptChallengeCommandDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentReceiptChallengeView"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipmentReceipt: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListShipmentReceiptRecipients: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptRecipientQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentRecipientPage"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverShipmentReceiptChallenge: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptChallengeStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ConfirmShipmentReceipt: {
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
            path: {
                challengeId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptConfirmDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReviewShipmentReceiptChallenge: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                challengeId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ReceiptChallengeReviewDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptReviewDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverShipmentReceipt: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ReceiptStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    IssueShipmentDocument: {
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
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ShipmentDocumentCommandDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentDocumentDto"];
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
            /** @description Payload Too Large */
            413: {
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
    DownloadShipmentDocument: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                documentId: string;
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/html": string;
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipmentDocument: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                shipmentId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentDocumentStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverShipmentDocument: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentDocumentStateDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadShipment: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentReadDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListShipments: {
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
                "application/json": components["schemas"]["PreparationPageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ShipmentReadPageDto"];
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
            /** @description Payload Too Large */
            413: {
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
    StockSourceAliasReceipt: {
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
                "application/json": components["schemas"]["StockSourceAliasRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceAliasReceiptDto"];
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
    ApproveStockSourceAlias: {
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
                    "application/json": components["schemas"]["StockSourceAliasCommandDto"];
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
    RejectStockSourceAlias: {
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
                    "application/json": components["schemas"]["StockSourceAliasCommandDto"];
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
    WithdrawStockSourceAlias: {
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
                    "application/json": components["schemas"]["StockSourceAliasCommandDto"];
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
    StockSourcePolicyReceipt: {
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
                "application/json": components["schemas"]["StockSourcePolicyRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyReceiptDto"];
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
    ApproveStockSourcePolicy: {
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
                    "application/json": components["schemas"]["StockSourcePolicyCommandDto"];
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
    RejectStockSourcePolicy: {
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
                    "application/json": components["schemas"]["StockSourcePolicyCommandDto"];
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
    WithdrawStockSourcePolicy: {
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
                    "application/json": components["schemas"]["StockSourcePolicyCommandDto"];
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
    StockSourceAliasProposal: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceAliasHistoryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceAliasDetailDto"] | null;
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
    StockSourceAliasHistory: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceAliasHistoryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceAliasPageDto"];
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
    ProposeStockSourceAlias: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceAliasProposeDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceAliasCommandDto"];
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
    StockSourceAliasCurrent: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceAliasReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceAliasCurrentDto"];
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
    StockSourcePolicyPreview: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourcePolicyPreviewRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyPreviewDto"];
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
    ProposeStockSourcePolicy: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourcePolicyProposeDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyCommandDto"];
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
    StockSourcePolicyDetail: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourcePolicyReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyDetailDto"] | null;
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
    StockSourcePolicyReview: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourcePolicyReviewRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyReviewDto"];
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
    StockSourcePolicyHistory: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourcePolicyHistoryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyPageDto"];
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
    StockSourcePolicyLatest: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourcePolicyReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourcePolicyLatestDto"];
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
    StockSourceCurrentRead: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceCurrentQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceCurrentDto"];
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
    ProposeStockSourceRevision: {
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
                poolId: string;
            };
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
                    "application/json": components["schemas"]["StockSourceRevisionCommandDto"];
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
    SuspendStockSource: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSuspensionRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSuspensionCommandDto"];
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
    StockSourceSuspensionPreview: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSuspensionPreviewRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSuspensionPreviewDto"];
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
    StockSourceSuspensionLatest: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSuspensionReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSuspensionDetailDto"] | null;
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
    StockSourceSuspensionDetail: {
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
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSuspensionReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSuspensionDetailDto"] | null;
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
    StockSourceSuspensionHistory: {
        parameters: {
            query?: never;
            header: {
                /** @description Fresh same-origin anti-forgery token from GET /api/v1/identity/csrf; the matching cookie and exact Origin are also required when identity is enabled. */
                "X-CSRF-Token": string;
                /** @description The account reference that reviewed this request. Compared with the verified session; never used to authenticate. */
                "X-Expected-Actor-Id": string;
            };
            path: {
                poolId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StockSourceSuspensionHistoryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSuspensionPageDto"];
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
    StockSourceCurrentList: {
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
                "application/json": components["schemas"]["StockSourceCurrentQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceCurrentPageDto"];
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
    StockSourceRevisionReceipt: {
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
                "application/json": components["schemas"]["StockSourceRevisionRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceRevisionReceiptDto"];
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
    ApproveStockSourceRevision: {
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
                    "application/json": components["schemas"]["StockSourceRevisionCommandDto"];
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
    StockSourceRevisionRead: {
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
                "application/json": components["schemas"]["StockSourceRevisionReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceRevisionDetailDto"] | null;
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
    RejectStockSourceRevision: {
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
                    "application/json": components["schemas"]["StockSourceRevisionCommandDto"];
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
    WithdrawStockSourceRevision: {
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
                    "application/json": components["schemas"]["StockSourceRevisionCommandDto"];
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
    StockSourceRevisionHistory: {
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
                "application/json": components["schemas"]["StockSourceRevisionQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceRevisionPageDto"];
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
    StockSourceSuspensionReceipt: {
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
                "application/json": components["schemas"]["StockSourceSuspensionRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceSuspensionReceiptDto"];
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
    StockSourceWarehouseChoices: {
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
                "application/json": components["schemas"]["StockSourceWarehouseQueryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StockSourceWarehousePageDto"];
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
    OrderBuyingContexts: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderContextsDto"];
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
            /** @description Payload Too Large */
            413: {
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
    SaveOrderDraft: {
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
                "application/json": components["schemas"]["OrderDraftSaveDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderDraftResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    OrderDraftRead: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderDraftDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverOrderDraftSave: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderDraftReceiptDto"];
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
            /** @description Payload Too Large */
            413: {
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
    OrderDraftList: {
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
                "application/json": components["schemas"]["OrderDraftListDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderDraftPageDto"];
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
            /** @description Payload Too Large */
            413: {
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
    SubmitBranchOrder: {
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
                "application/json": components["schemas"]["OrderSubmitDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderSubmitResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ReadOwnBranchOrder: {
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
                "application/json": components["schemas"]["OrderReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SubmittedOrderDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverBranchOrder: {
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
                "application/json": components["schemas"]["OrderDraftRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderSubmitRecoveryDto"];
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
            /** @description Payload Too Large */
            413: {
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
    OrderCheckoutPreview: {
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
                "application/json": components["schemas"]["OrderCheckoutDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderCheckoutReviewDto"];
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
            /** @description Payload Too Large */
            413: {
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
    ListOwnBranchOrders: {
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
                "application/json": components["schemas"]["OwnOrderListDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SubmittedOrderPageDto"];
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
            /** @description Payload Too Large */
            413: {
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
    OrderQuotePreview: {
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
                "application/json": components["schemas"]["OrderQuoteRequestDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderQuoteDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecordOrderDecision: {
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
                "application/json": components["schemas"]["OrderDecisionDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderDecisionResultDto"];
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
            /** @description Payload Too Large */
            413: {
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
    OrderReviewRead: {
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
                "application/json": components["schemas"]["OrderReviewReadDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderReviewDto"];
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
            /** @description Payload Too Large */
            413: {
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
    RecoverOrderDecision: {
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
                "application/json": components["schemas"]["OrderDecisionRecoveryDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderDecisionReceiptDto"];
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
            /** @description Payload Too Large */
            413: {
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
    OrderReviewList: {
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
                "application/json": components["schemas"]["OrderReviewListDto"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OrderReviewPageDto"];
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
            /** @description Payload Too Large */
            413: {
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
