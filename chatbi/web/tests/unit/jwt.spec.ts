import { describe, expect, it } from 'vitest';
import { decodeJwtClaims, extractRoles, isTokenExpired } from '@/utils/jwt';
import { makeMockJwt } from '../helpers';

describe('JWT 解析（前端守卫用，不验签；服务端强制验签为准）', () => {
  it('解析 claims：sub/union_id/role[]/dept_path/token_ver/exp（基线 3.4 结构）', () => {
    const token = makeMockJwt('DataSteward');
    const claims = decodeJwtClaims(token);
    expect(claims).not.toBeNull();
    expect(claims!.sub).toBe('1');
    expect(claims!.union_id).toBe('mock_union_datasteward');
    expect(claims!.dept_path).toBe('/总部/技术部');
    expect(claims!.token_ver).toBe(1);
    expect(extractRoles(claims)).toEqual(['DataSteward']);
  });

  it('role 单值字符串兼容为数组', () => {
    const b64 = (o: unknown) =>
      btoa(unescape(encodeURIComponent(JSON.stringify(o))))
        .replace(/\+/g, '-')
        .replace(/\//g, '_')
        .replace(/=+$/, '');
    const token = `${b64({ alg: 'HS256' })}.${b64({ sub: '2', role: 'SysAdmin' })}.sig`;
    expect(extractRoles(decodeJwtClaims(token))).toEqual(['SysAdmin']);
  });

  it('非法 token → null；过期判定含 30s 余量', () => {
    expect(decodeJwtClaims('not-a-jwt')).toBeNull();
    expect(extractRoles(null)).toEqual([]);
    const expired = makeMockJwt('BusinessUser', { exp: Math.floor(Date.now() / 1000) - 10 });
    expect(isTokenExpired(decodeJwtClaims(expired))).toBe(true);
    const fresh = makeMockJwt('BusinessUser');
    expect(isTokenExpired(decodeJwtClaims(fresh))).toBe(false);
    // 60s 后过期 → 30s 余量内视为需要续期
    const soon = makeMockJwt('BusinessUser', { exp: Math.floor(Date.now() / 1000) + 20 });
    expect(isTokenExpired(decodeJwtClaims(soon), 30)).toBe(true);
  });
});
