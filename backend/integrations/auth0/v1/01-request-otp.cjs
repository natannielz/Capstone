"use strict";

// Draft: bind before 02-assert-otp in the same post-login flow. Deployment requires
// the tenant acceptance matrix; these files do not represent a configured tenant.
exports.onExecutePostLogin = async (event, api) => {
  const secrets = event.secrets || {};
  if (!secrets.UNIT_TOKO_CLIENT_ID || event.client?.client_id !== secrets.UNIT_TOKO_CLIENT_ID) return;
  const deny = () => api.access.deny("Unit Toko could not verify the required sign-in checks.");
  const challenge = event.request?.query?.unit_toko_challenge;
  const namespace = secrets.UNIT_TOKO_CLAIMS_NAMESPACE;
  const policyVersion = secrets.UNIT_TOKO_POLICY_VERSION;
  if (!secrets.UNIT_TOKO_CONNECTION_ID || event.connection?.id !== secrets.UNIT_TOKO_CONNECTION_ID ||
      event.connection?.strategy !== "auth0" || !secrets.UNIT_TOKO_CALLBACK_URI ||
      event.transaction?.redirect_uri !== secrets.UNIT_TOKO_CALLBACK_URI ||
      event.transaction?.protocol !== "oidc-basic-profile" ||
      !Array.isArray(event.transaction?.response_type) || event.transaction.response_type.length !== 1 || event.transaction.response_type[0] !== "code" ||
      typeof challenge !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(challenge) ||
      typeof namespace !== "string" || !validNamespace(namespace) ||
      typeof policyVersion !== "string" || !/^[A-Za-z0-9._-]{1,40}$/.test(policyVersion) ||
      event.user?.email_verified !== true || typeof api.transaction?.setMetadata !== "function") return deny();
  const methods = Array.isArray(event.authentication?.methods) ? event.authentication.methods : [];
  api.transaction.setMetadata("unitTokoOtpV1", {
    challenge, requestedAt: Date.now(), policyVersion,
    priorMethods: methods.filter(method => method.name === "mfa").map(method => `${method.name}|${method.type}|${method.timestamp}`),
  });
  const factors = Array.isArray(event.user?.enrolledFactors) ? event.user.enrolledFactors : [];
  if (factors.some(factor => factor.type === "otp")) api.authentication.challengeWith({ type: "otp" });
  else api.authentication.enrollWith({ type: "otp" });
};

function validNamespace(value) {
  try {
    const uri = new URL(value);
    return uri.protocol === "https:" && !uri.username && !uri.password && !uri.search && !uri.hash && value.endsWith("/") &&
      !["auth0.com", "webtask.io", "webtask.run"].some(host => uri.hostname === host || uri.hostname.endsWith(`.${host}`));
  } catch { return false; }
}
