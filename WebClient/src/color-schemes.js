export const COLOR_SCHEME_NAMES = Object.freeze({
    DEFAULT: 'Default',
    INVERTED: 'Inverted',
    BLACK: 'Black',
    WHITE: 'White'
});

export const COLOR_SCHEMES = Object.freeze({
    [COLOR_SCHEME_NAMES.DEFAULT]: Object.freeze({
        backgroundColor: '#303236',
        minorGridColor: 'rgba(255, 255, 255, 0.08)',
        majorGridColor: 'rgba(255, 255, 255, 0.14)'
    }),
    [COLOR_SCHEME_NAMES.INVERTED]: Object.freeze({
        backgroundColor: '#ffffff',
        minorGridColor: 'rgba(48, 50, 54, 0.08)',
        majorGridColor: 'rgba(48, 50, 54, 0.14)'
    }),
    [COLOR_SCHEME_NAMES.BLACK]: Object.freeze({
        backgroundColor: '#000000',
        minorGridColor: '#000000',
        majorGridColor: '#000000'
    }),
    [COLOR_SCHEME_NAMES.WHITE]: Object.freeze({
        backgroundColor: '#ffffff',
        minorGridColor: '#ffffff',
        majorGridColor: '#ffffff'
    })
});

export function resolveColorScheme(name) {
    return Object.hasOwn(COLOR_SCHEMES, name)
        ? COLOR_SCHEMES[name]
        : COLOR_SCHEMES[COLOR_SCHEME_NAMES.DEFAULT];
}

export function buildGridBackgroundImage(scheme) {
    return [
        `linear-gradient(${scheme.minorGridColor} 1px, transparent 1px)`,
        `linear-gradient(90deg, ${scheme.minorGridColor} 1px, transparent 1px)`,
        `linear-gradient(${scheme.majorGridColor} 1px, transparent 1px)`,
        `linear-gradient(90deg, ${scheme.majorGridColor} 1px, transparent 1px)`
    ].join(', ');
}
