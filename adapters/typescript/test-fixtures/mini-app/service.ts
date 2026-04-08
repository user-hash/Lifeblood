import { IRepository, Entity } from './domain';

export class UserService {
  constructor(private repo: IRepository) {}

  getUser(id: string): Entity | null {
    return this.repo.findById(id);
  }
}
