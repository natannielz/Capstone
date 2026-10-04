import { BuyerCatalog } from '../../modules/catalog/BuyerCatalog';
import { isPilotRelease } from '../../lib/release-scope';

export const metadata = { title: 'Katalog kemasan | Unit Toko' };
export default function ShopPage() { return <BuyerCatalog returnPath="/shop" requestOnly={isPilotRelease} />; }
