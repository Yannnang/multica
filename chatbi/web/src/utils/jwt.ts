import type { JwtClaims, RoleCode } from '@/api/types';

function base64UrlDecode(input: string): string {
  const b64 = input.replace(/-/g, '+').replace(/_/g, '/');
  const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
  const bin = atob(padded);
  // UTF-8 安全解码
  const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0));
  return new TextDecoder('utf-8').decode(bytes);
}

/**
 * 解析 JWT payload（不验签——服务端仍强制验签与 TokenVersion 校验，
 * 前端解码仅用于路由守卫与展示，基线 3.4）。
 */
export function decodeJwtClaims(token: string): JwtClaims | null {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) return null;
    const payload = JSON.parse(base64UrlDecode(parts[1])) as JwtClaims;
    return payload && typeof payload === 'object' ? payload : null;
  } catch {
    return null;
  }
}

/** 从 claims 提取角色数组（role claim 兼容单值/数组/数字编码） */
export function extractRoles(claims: JwtClaims | null): RoleCode[] {
  if (!claims) return [];
  const raw = claims.role;
  const arr = Array.isArray(raw) ? raw : raw != null ? [raw] : [];
  return arr.filter((r): r is RoleCode => typeof r === 'string');
}

/** token 是否已过期（exp 秒级；留 30s 余量触发静默续期） */
export function isTokenExpired(claims: JwtClaims | null, skewSeconds = 30): boolean {
  if (!claims || typeof claims.exp !== 'number') return false;
  return claims.exp * 1000 <= Date.now() + skewSeconds * 1000;
}
