"use strict";

// Run only after 01-request-otp. Assurance comes from a newly completed provider
// method, never from enrollment flags or the current clock.
exports.onExecutePostLogin = async (event, api) => {
  const secrets = event.secrets || {};
  if (!secrets.UNIT_TOKO_CLIENT_ID || event.client?.client_id !== secrets.UNIT_TOKO_CLIENT_ID) return;
  const deny = () => api.access.deny("Unit Toko could not verify the completed second factor.");
  const stage = event.transaction?.metadata?.unitTokoOtpV1;
  if (!stage || typeof stage.challenge !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(stage.challenge) ||
      stage.challenge !== event.request?.query?.unit_toko_challenge || stage.policyVersion !== secrets.UNIT_TOKO_POLICY_VERSION ||
      !Number.isSafeInteger(stage.requestedAt) || stage.requestedAt <= 0 ||
      !Array.isArray(stage.priorMethods) || stage.priorMethods.some(value => typeof value !== "string") ||
      event.connection?.id !== secrets.UNIT_TOKO_CONNECTION_ID || event.connection?.strategy !== "auth0" ||
      event.user?.email_verified !== true || typeof secrets.UNIT_TOKO_CLAIMS_NAMESPACE !== "string") return deny();
  const now = Date.now();
  if (stage.requestedAt > now + 1000 || now - stage.requestedAt > 10 * 60 * 1000) return deny();
  const methods = Array.isArray(event.authentication?.methods) ? event.authentication.methods : [];
  const completed = methods.filter(method => {
    if (method.name !== "mfa" || method.type !== "otp" || typeof method.timestamp !== "string" ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/.test(method.timestamp)) return false;
    const at = Date.parse(method.timestamp);
    return Number.isFinite(at) && at >= stage.requestedAt - 1000 && at <= now + 1000 &&
      !stage.priorMethods.includes(`${method.name}|${method.type}|${method.timestamp}`);
  }).sort((left, right) => Date.parse(right.timestamp) - Date.parse(left.timestamp));
  if (completed.length === 0) return deny();
  api.idToken.setCustomClaim(`${secrets.UNIT_TOKO_CLAIMS_NAMESPACE}assurance`, {
    policyVersion: stage.policyVersion, connectionId: event.connection.id, method: "otp",
    factorCompletedAt: Math.floor(Date.parse(completed[0].timestamp) / 1000), challengeBinding: stage.challenge,
  });
};
