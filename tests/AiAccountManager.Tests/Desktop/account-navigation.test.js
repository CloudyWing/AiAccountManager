const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const calls = [];
const context = {
  window: { location: { search: '?account=11111111-1111-1111-1111-111111111111' } },
  URLSearchParams,
  document: { getElementById: id => ({ scrollIntoView: () => calls.push(id) }) }
};
vm.runInNewContext(fs.readFileSync('src/AiAccountManager.Desktop/wwwroot/account-navigation.js', 'utf8'), context);
context.window.accountNavigation.scrollTo('codex-account-11111111111111111111111111111111', '11111111-1111-1111-1111-111111111111');
assert.deepEqual(calls, ['codex-account-11111111111111111111111111111111']);
context.window.location.search = '?account=22222222-2222-2222-2222-222222222222';
context.window.accountNavigation.scrollTo('codex-account-11111111111111111111111111111111', '11111111-1111-1111-1111-111111111111');
assert.equal(calls.length, 1);
context.window.accountNavigation.scrollTo('claude-account-22222222222222222222222222222222', '22222222-2222-2222-2222-222222222222');
assert.equal(calls[1], 'claude-account-22222222222222222222222222222222');
console.log('account-navigation probe passed');
