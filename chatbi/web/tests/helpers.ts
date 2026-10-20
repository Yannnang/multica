/** 测试辅助：构造 mock JWT（不验签，仅承载 role/token_ver claim，与 msw handlers 约定一致） */
export function makeMockJwt(
  role: string,
  opts?: { exp?: number; tokenVer?: number },
): string {
  const b64 = (o: unknown) =>
    btoa(unescape(encodeURIComponent(JSON.stringify(o))))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
  const exp = opts?.exp ?? Math.floor(Date.now() / 1000) + 3600;
  return [
    b64({ alg: 'HS256', typ: 'JWT' }),
    b64({
      sub: '1',
      union_id: `mock_union_${role.toLowerCase()}`,
      role: [role],
      dept_path: '/总部/技术部',
      token_ver: opts?.tokenVer ?? 1,
      exp,
    }),
    'mock-signature',
  ].join('.');
}
