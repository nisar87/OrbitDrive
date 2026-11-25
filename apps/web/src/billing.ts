import { callJson, type Session } from './api';

/** A limit of null means "no limit". Sizes are bytes. */
export interface Limits { pcs: number | null; devices: number | null; filesPerDay: number | null; bytesPerFile: number | null; bytesPerDay: number | null }
export interface PriceView { months: number; usd: number; pkr: number; pkrApprox: boolean }
export interface PlanView { id: string; name: string; purchasable: boolean; limits: Limits; prices: PriceView[] }
export interface PlansView { currency: string; pkrPerUsd: number; rateUpdatedAt: string; limitsEnabled: boolean; plans: PlanView[] }
export interface AccountPlan {
  planId: string; planName: string; state: 'free' | 'active' | 'grace'; endsAt: string | null; graceEndsAt: string | null; daysLeft: number; limits: Limits;
  filesToday: number; bytesToday: number; resetsAt: string; pcs: number; devices: number; limitsEnabled: boolean; paymentInstructions: string;
}
export interface Quote { planId: string; planName: string; months: number; listUsd: number; dueUsd: number; listPkr: number; duePkr: number; couponCode: string | null; discountUsd: number }
export interface Order {
  id: string; subject: string; planId: string; months: number; listUsd: number; dueUsd: number; duePkr: number; couponCode: string | null; status: string;
  createdAt: string; method: string | null; reference: string | null; licenceId: string | null; decidedBy: string | null; decidedAt: string | null; reason: string | null;
}
export interface Coupon { code: string; kind: 'percent' | 'fixed'; value: number; planIds: string[]; expiresAt: string | null; maxRedemptions: number | null; redeemed: number; active: boolean; note: string | null }
export interface EditablePlan { id: string; name: string; rank: number; limits: Limits; prices: { months: number; usd: number; pkrOverride: number | null }[]; purchasable: boolean }
export interface AdminBilling { plans: PlansView; editable: EditablePlan[]; coupons: Coupon[]; openOrders: number; paymentInstructions: string }
export interface Licence { id: string; subject: string; planId: string; startsAt: string; endsAt: string; source: string; note: string | null }
export interface UserBilling { uid: string; plan: AccountPlan; passes: Licence[]; orders: Order[] }

export const MIB = 1024 * 1024;
export const GIB = 1024 * MIB;

export const PAYMENT_METHODS = [['jazzcash', 'JazzCash'], ['easypaisa', 'Easypaisa'], ['bank', 'Bank transfer'], ['raast', 'Raast'], ['other', 'Other']] as const;

export function formatUsd(value: number): string { return Number.isInteger(value) ? `$${value}` : `$${value.toFixed(2)}`; }
export function formatPkr(value: number, approx: boolean): string { return `${approx ? '≈ ' : ''}PKR ${Math.round(value).toLocaleString('en-US')}`; }
/** "$3.50 (PKR 1,000)": dollars first, rupees beside them, approximate when they follow the exchange rate. */
export function priceLabel(price: Pick<PriceView, 'usd' | 'pkr' | 'pkrApprox'>): string { return `${formatUsd(price.usd)} (${formatPkr(price.pkr, price.pkrApprox)})`; }
export function monthsLabel(months: number): string { return months === 1 ? '1 month' : months === 12 ? '12 months' : `${months} months`; }

export function formatSize(bytes: number): string {
  if (bytes >= GIB) return `${+(bytes / GIB).toFixed(1)} GB`;
  if (bytes >= MIB) return `${+(bytes / MIB).toFixed(1)} MB`;
  return `${Math.round(bytes / 1024)} KB`;
}
/** A limit for display: null is "No limit". */
export function limitText(value: number | null, kind: 'count' | 'bytes'): string { return value === null ? 'No limit' : kind === 'bytes' ? formatSize(value) : String(value); }
/** How full a limit is, 0 to 100, or null when there is no limit. */
export function percentUsed(used: number, limit: number | null): number | null { return limit === null || limit <= 0 ? null : Math.min(100, Math.round((used / limit) * 100)); }

export const billingApi = {
  plans: () => callJson<PlansView>(null, 'GET', '/v1/plans'),
  myPlan: (s: Session) => callJson<AccountPlan>(s, 'GET', '/v1/account/plan'),
  myOrders: (s: Session) => callJson<Order[]>(s, 'GET', '/v1/account/orders'),
  quote: (s: Session, planId: string, months: number, coupon: string | null) => callJson<Quote>(s, 'POST', '/v1/account/quote', { planId, months, coupon }),
  order: (s: Session, planId: string, months: number, coupon: string | null, method: string | null) => callJson<Order>(s, 'POST', '/v1/account/orders', { planId, months, coupon, method }),
  proof: (s: Session, id: string, method: string, reference: string) => callJson<Order>(s, 'POST', `/v1/account/orders/${encodeURIComponent(id)}/proof`, { method, reference }),
  cancel: (s: Session, id: string) => callJson<Order>(s, 'POST', `/v1/account/orders/${encodeURIComponent(id)}/cancel`),
  admin: (s: Session) => callJson<AdminBilling>(s, 'GET', '/v1/admin/billing'),
  setPlans: (s: Session, plans: EditablePlan[], reason: string) => callJson<{ ok: boolean }>(s, 'PUT', '/v1/admin/billing/plans', { plans, reason }),
  setRate: (s: Session, pkrPerUsd: number, reason: string) => callJson<{ ok: boolean }>(s, 'PUT', '/v1/admin/billing/rate', { pkrPerUsd, reason }),
  setLimits: (s: Session, enabled: boolean, reason: string) => callJson<{ ok: boolean }>(s, 'PUT', '/v1/admin/billing/limits', { enabled, reason }),
  setInstructions: (s: Session, text: string, reason: string) => callJson<{ ok: boolean }>(s, 'PUT', '/v1/admin/billing/instructions', { text, reason }),
  saveCoupon: (s: Session, code: string, body: { kind: string; value: number; planIds: string[]; expiresAt: string | null; maxRedemptions: number | null; active: boolean; note: string | null; reason: string }) =>
    callJson<Coupon>(s, 'PUT', `/v1/admin/coupons/${encodeURIComponent(code)}`, body),
  orders: (s: Session, status: string | null) => callJson<Order[]>(s, 'GET', `/v1/admin/orders${status ? `?status=${encodeURIComponent(status)}` : ''}`),
  verify: (s: Session, id: string, reason: string) => callJson<Order>(s, 'POST', `/v1/admin/orders/${encodeURIComponent(id)}/verify`, { reason }),
  reject: (s: Session, id: string, reason: string) => callJson<Order>(s, 'POST', `/v1/admin/orders/${encodeURIComponent(id)}/reject`, { reason }),
  userBilling: (s: Session, uid: string) => callJson<UserBilling>(s, 'GET', `/v1/admin/users/${encodeURIComponent(uid)}/billing`),
  addPass: (s: Session, uid: string, planId: string, months: number, reason: string, source: 'manual' | 'grant') =>
    callJson<Licence>(s, 'POST', `/v1/admin/users/${encodeURIComponent(uid)}/passes`, { planId, months, reason, source }),
  endPass: (s: Session, id: string, skipGrace: boolean, reason: string) => callJson<{ ok: boolean }>(s, 'POST', `/v1/admin/passes/${encodeURIComponent(id)}/end`, { skipGrace, reason }),
};
