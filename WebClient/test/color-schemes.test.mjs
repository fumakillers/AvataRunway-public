import assert from 'node:assert/strict';
import test from 'node:test';
import {
    buildGridBackgroundImage,
    COLOR_SCHEME_NAMES,
    COLOR_SCHEMES,
    resolveColorScheme
} from '../src/color-schemes.js';

test('defines the four supported color scheme names once', () => {
    assert.deepEqual(Object.values(COLOR_SCHEME_NAMES), [
        'Default',
        'Inverted',
        'Black',
        'White'
    ]);
});

test('Default preserves the existing viewer colors', () => {
    assert.deepEqual(COLOR_SCHEMES.Default, {
        backgroundColor: '#303236',
        minorGridColor: 'rgba(255, 255, 255, 0.08)',
        majorGridColor: 'rgba(255, 255, 255, 0.14)'
    });
});

test('Inverted exchanges the light and dark sides of Default', () => {
    assert.deepEqual(COLOR_SCHEMES.Inverted, {
        backgroundColor: '#ffffff',
        minorGridColor: 'rgba(48, 50, 54, 0.08)',
        majorGridColor: 'rgba(48, 50, 54, 0.14)'
    });
});

test('Black and White use identical background and grid colors', () => {
    for (const name of ['Black', 'White']) {
        const scheme = COLOR_SCHEMES[name];
        assert.equal(scheme.minorGridColor, scheme.backgroundColor);
        assert.equal(scheme.majorGridColor, scheme.backgroundColor);
    }
});

test('unknown, empty, and missing names fall back to Default', () => {
    assert.equal(resolveColorScheme('Unknown'), COLOR_SCHEMES.Default);
    assert.equal(resolveColorScheme(''), COLOR_SCHEMES.Default);
    assert.equal(resolveColorScheme(undefined), COLOR_SCHEMES.Default);
    assert.equal(resolveColorScheme('toString'), COLOR_SCHEMES.Default);
    assert.equal(resolveColorScheme('__proto__'), COLOR_SCHEMES.Default);
});

test('grid CSS uses both minor and major colors', () => {
    const css = buildGridBackgroundImage(COLOR_SCHEMES.Default);
    assert.equal(css.match(/linear-gradient/g)?.length, 4);
    assert.match(css, /rgba\(255, 255, 255, 0\.08\)/);
    assert.match(css, /rgba\(255, 255, 255, 0\.14\)/);
});
