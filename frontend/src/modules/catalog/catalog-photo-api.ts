import {createUnitTokoClient} from '../../api/client';
import {createCatalogPhotoGateway, type CatalogPhotoTransport} from './catalog-photo-client';

/** Generated same-origin transport supplies a fresh CSRF token and never retries photo commands. */
export function createCatalogPhotoApiGateway() {
  const client = createUnitTokoClient();
  const transport: CatalogPhotoTransport = {
    post(request) {
      const options = {credentials: request.credentials, cache: request.cache, redirect: request.redirect, signal: request.signal};
      switch (request.path) {
        case '/api/v1/catalog/skus/{id}/primary-photo/query':
          return client.POST(request.path, {...options, params: request.params, body: request.body});
        case '/api/v1/catalog/skus/{id}/primary-photo':
          return client.POST(request.path, {...options, params: request.params, body: request.body});
        case '/api/v1/catalog/primary-photos/operations/query':
          return client.POST(request.path, {...options, params: request.params, body: request.body});
      }
    },
  };
  return createCatalogPhotoGateway(transport);
}
