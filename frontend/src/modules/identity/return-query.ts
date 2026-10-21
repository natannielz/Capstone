// The record a page has open (?draft=7, ?order=41, ?package=<uuid>) survives sign-in. Same grammar as the API's
// ReturnDestination.IsSafeQuery: at most four name=value pairs, plain letters, digits, '-' and '_' only, so a return
// destination can never carry another address, an encoded character or a fragment.
const returnQuery = /^[a-z][A-Za-z0-9]{0,31}=[A-Za-z0-9_-]{1,64}(?:&[a-z][A-Za-z0-9]{0,31}=[A-Za-z0-9_-]{1,64}){0,3}$(?![\s\S])/;

export const isReturnQuery = (query: string) => returnQuery.test(query);
