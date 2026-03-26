class HscScriptError extends Error {
    constructor(code, message) {
        super(message);
        this.error_code = code;
        this.error_msg = message;
    }
}

function parseUpdateText(text) {
    const parsed = {
        release_date: '',
        kb: '',
        build: '',
        release_type: 'Monthly'
    };
    
    const dateMatch = text.match(/([A-Za-z]+ \d{1,2}, \d{4})/);
    if (dateMatch) {
        const date = new Date(dateMatch[1]);
        if (!isNaN(date.getTime())) {
            parsed.release_date = date.toISOString().split('T')[0];
        }
    }
    
    const kbMatch = text.match(/KB\s*(\d+)/i);
    if (kbMatch) parsed.kb = kbMatch[1];
    
    const buildMatch = text.match(/\(OS Build[s]? ([^)]+)\)/i);
    if (buildMatch) {
        const builds = buildMatch[1].match(/\d+\.\d+/g);
        if (builds) parsed.build = builds.join('|');
    }
    
    const typeMatch = text.match(/\)\s*(.+)$/);
    if (typeMatch && typeMatch[1].trim()) {
        parsed.release_type = typeMatch[1].trim();
    }
    
    return parsed;
}

try {
    const results = [];
    const seen = new Set();
    const excluded = new Set();
    
    const categories = document.querySelectorAll('.supLeftNavCategory');
    if (categories.length === 0) {
        throw new HscScriptError(-1, 'NO_SUPLEFTNAVCATEGORY_ELEMENTS_FOUND');
    }
    
    categories.forEach(category => {
        category.querySelectorAll('.supLeftNavLink').forEach(link => {
            if (link.closest('li')?.classList.contains('supLeftNavCurrentArticle') || 
                link.getAttribute('aria-current') === 'page') {
                excluded.add(link.textContent.trim());
            }
        });
    });
    
    categories.forEach(category => {
        const categoryTitle = category.querySelector('.supLeftNavCategoryTitle');
        let productName = categoryTitle?.textContent.trim() || 'Unknown Product';
        
        productName = productName.replace(/\u00a0/g, ' ').replace(/\s*update history\s*/i, '');
        
        category.querySelectorAll('.supLeftNavLink').forEach(link => {
            let linkText = link.textContent.trim().replace(/\u00a0/g, ' ');
            
            if (!linkText || excluded.has(linkText) || 
                linkText === productName ||
                linkText.includes('version') && linkText.includes('update history') ||
                linkText.match(/^Windows \d+, version \d+H\d+$/) ||
                linkText.toLowerCase().includes('end of servic') ||
                linkText.toLowerCase().includes('windows 10 mobile')) {
                return;
            }
            
            let href = link.href;
            if (href.startsWith('/')) href = document.location.origin + href;
            
            const key = `${productName}|${linkText}|${href}`;
            if (seen.has(key)) return;
            seen.add(key);
            
            const parsed = parseUpdateText(linkText);
            if (!parsed.kb) return;
            results.push({
                text: linkText,
                href: href,
                product_name: productName,
                ...parsed
            });
        });
    });
    
    if (results.length === 0) {
        throw new HscScriptError(-2, 'NO_VALID_SUPLEFTNAVLINK_FOUND');
    }
    
    return JSON.stringify({
        items: results,
        error_code: 0,
        error_msg: ""
    });
    
} catch (error) {
    return JSON.stringify({
        items: [],
        error_code: error instanceof HscScriptError ? error.error_code : -99,
        error_msg: error instanceof HscScriptError ? error.error_msg : 'UNKNOWN_ERROR'
    });
}
